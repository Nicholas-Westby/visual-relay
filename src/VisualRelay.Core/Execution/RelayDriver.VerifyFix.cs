using System.Diagnostics;
using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>10× multiplier for tasks whose id appears in
    /// <see cref="RelayConfig.BoostTurnsTaskIds"/>.</summary>
    private const int TurnBoostMultiplier = 10;
    /// <summary>
    /// Runs the fix-verify loop for stage 10. Each iteration is an ESCALATION RUN: it
    /// bumps the tier (cheap→balanced→frontier, capped) and doubles the turn + ceiling
    /// budget (flat under the 10× boost) via <see cref="StageEscalation"/>, re-verifies,
    /// and on red escalates again — up to <see cref="RelayConfig.MaxStageFailures"/> runs,
    /// then flags. (The old fixed <c>MaxVerifyLoops</c> COUNT is subsumed by the 3-run
    /// cap; MaxVerifyLoops now only gates whether this loop is entered at all — see
    /// RunTaskAsync. The non-convergence early-flag is gone: a higher tier may change the
    /// verdict, so every run is spent before flagging.) Returns null outcome on green;
    /// a Flagged outcome when the runs are exhausted or a non-retryable / hard-abort
    /// failure occurs (timeout / invalid subagent / absolute-ceiling / socket wedge).
    /// </summary>
    private async Task<(RelayTaskOutcome? Outcome, string PreviousSeal, string TaskHash, double SessionCostUsd, int UnknownCostStageCount)> RunVerifyFixLoopAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayTaskInput input,
        StringBuilder ledger,
        List<string> seals,
        List<StageStatusEntry> statusEntries,
        IReadOnlyList<string> manifest,
        string previousSeal,
        string taskHash,
        double sessionCostUsd,
        int unknownCostStageCount,
        string failingTestOutput,
        // Absolute path to the persisted FULL output behind failingTestOutput (from the
        // stage-9 gate for the first iteration, then each red stage-10 attempt). Surfaced
        // in the agent prompt so it can read the complete log. Null when persistence failed.
        string? failingVerifyOutputPath,
        string? bootstrapCheckCmd,
        string? guardCmd,
        string pinnedSwivalProfileContent,
        CancellationToken cancellationToken)
    {
        var stage = RelayStages.All[9]; // Stage 10 — Fix-verify
        // The run cap is MaxStageFailures (the 3-run escalation model). The effective
        // run-1 turn/ceiling base is the (already-boost-applied) config budget; the
        // per-run doubling is suppressed in flat 10× mode while the tier escalates.
        var maxRuns = Math.Max(1, config.MaxStageFailures);
        var boosted = config.BoostTurnsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true;
        var baseTurns = boosted ? SaturatingBoost(config.MaxTurns) : config.MaxTurns;
        var baseCeilingMs = boosted ? SaturatingBoost(config.SubagentTimeoutMilliseconds) : config.SubagentTimeoutMilliseconds;

        for (var run = 1; run <= maxRuns; run++)
        {
            var tier = StageEscalation.TierForRun(stage.Tier, run);
            var turns = StageEscalation.TurnsForRun(baseTurns, run, boosted);
            var ceilingMs = StageEscalation.Scale(baseCeilingMs, StageEscalation.RunMultiplier(run, boosted));
            if (run > 1)
            {
                await PublishStageEscalatedAsync(rootPath, runId, taskId, stage, run, maxRuns,
                    StageEscalation.TierForRun(stage.Tier, run - 1), tier,
                    StageEscalation.TurnsForRun(baseTurns, run - 1, boosted), turns, cancellationToken);
            }

            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "info", "stage_start", runId, rootPath, taskId,
                stage.Number, tier,
                Data: new Dictionary<string, string> { ["name"] = stage.Name }), cancellationToken);

            MarkStatus(statusEntries, stage.Number, "Running");
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, stage,
                input, ledger, manifest, lastTestOutput: failingTestOutput, testCommand: config.TestCommand,
                pinnedSwivalProfileContent: pinnedSwivalProfileContent, verifyOutputPath: failingVerifyOutputPath);
            // Pin this run's escalated tier + budget; MaxSelfEscalations=0 so the inner
            // RunAsync does not also escalate (this loop owns the stage-10 run budget).
            invocation = invocation with { Tier = tier, MaxTurns = turns, AbsoluteCeilingMs = ceilingMs, MaxSelfEscalations = 0 };
            var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
            var cost = TryEstimateCost(invocation.ReportFile);
            if (cost is not null) { sessionCostUsd += cost.CostUsd; } else { unknownCostStageCount++; }

            if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
            {
                // Hard infra abort (absolute ceiling / socket wedge) flags now — re-running
                // burns the budget. Any other in-process failure on a non-final run
                // escalates (a higher tier may yet produce a valid result); the final run
                // flags.
                if (result.HardAbort || run >= maxRuns)
                {
                    var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                        result.Error ?? "invalid subagent result", result.RawText, statusEntries, cancellationToken);
                    return (outcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
                }
                continue;
            }

            var body = result.Json;

            // Bootstrap re-check: runs before the test command. If it fails the
            // stage is red and the agent gets the bootstrap failure for the next
            // fix-verify iteration. The test command still runs so combined
            // failures are visible.
            TestRunResult? bootstrapFailingResult = null;
            string? guardFailureOutput = null;
            string? check = null;
            if (bootstrapCheckCmd is not null)
            {
                var bootstrapResult = await _dependencies.TestRunner.RunAsync(rootPath, bootstrapCheckCmd, cancellationToken);
                if (bootstrapResult.TimedOut)
                {
                    var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                        ErrorHintClassifier.WithHint(bootstrapResult.Output), null, statusEntries, cancellationToken);
                    return (outcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
                }

                if (bootstrapResult.ExitCode != 0)
                {
                    check = "red";
                    bootstrapFailingResult = bootstrapResult;
                }
            }

            // Guard re-check: runs after bootstrap, before test command.
            // Uses baseline diff so pre-existing violations don't block.
            if (guardCmd is not null)
            {
                var (newViolations, _, timedOut) = await RunGuardCheckAsync(
                    rootPath, taskId, runId, _dependencies.TestRunner, _dependencies.GitInvoker,
                    config.FormatCommand, guardCmd, config.BaselineVerify, cancellationToken);

                if (timedOut)
                {
                    var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                        ErrorHintClassifier.WithHint(newViolations ?? "guard timed out"), null, statusEntries, cancellationToken);
                    return (outcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
                }

                if (newViolations is not null)
                {
                    check = "red";
                    guardFailureOutput = newViolations;
                }
            }

            var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(
                rootPath, config, stageNumber: 10, attempt: run, runId, taskId, cancellationToken);
            await EmitMutatedTreeAdvisoryAsync(rootPath, runId, taskId, stage, verifyMutations, cancellationToken);
            var testDurationSeconds = (double?)testResult.Elapsed.TotalSeconds;
            if (testResult.TimedOut)
            {
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                    ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken);
                return (outcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
            }

            check ??= testResult.ExitCode == 0 ? "green" : "red";
            // The COMPLETE combined log persisted to this attempt's artifact: the full test
            // output PLUS any guard/bootstrap text — the full version of the trimmed tail the
            // NEXT iteration shows the agent (built below as failingTestOutput). Null on green.
            var attemptFullOutput = check == "red"
                ? BuildFullFailureOutput(testResult, guardFailureOutput, bootstrapFailingResult is not null, bootstrapFailingResult?.Output)
                : null;
            var attemptVerifyOutputPath = await PublishVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage, run, config, testResult, manifest, cancellationToken, overrideCheck: check, combinedFailureOutput: attemptFullOutput);

            // Record attempt in ledger with labeled section.
            var header = maxRuns > 1
                ? $"## Stage {stage.Number} - {stage.Name} (attempt {run}/{maxRuns})"
                : $"## Stage {stage.Number} - {stage.Name}";
            ledger.AppendLine(header);
            ledger.AppendLine();
            ledger.AppendLine(body);
            ledger.AppendLine();

            var treeHash = WorkingTreeHash(rootPath, manifest);
            var artifactHash = Hashing.Sha256Hex(stage.Number.ToString(), stage.Name, body);
            var seal = Hashing.Sha256Hex(previousSeal, stage.Number.ToString(), DateTimeOffset.UtcNow.ToString("O"), artifactHash, treeHash, check);
            previousSeal = seal;
            taskHash = seal;
            seals.Add(SerializeSeal(stage.Number, artifactHash, treeHash, seal, check));
            await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);

            stopwatch.Stop();
            MarkStatusDone(statusEntries, stage, stopwatch.Elapsed, cost, check, testDurationSeconds);
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);

            await PublishStageDoneAsync(rootPath, runId, taskId, stage, stopwatch.Elapsed, cost,
                sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);

            if (check == "green")
                return (null, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);

            // No early non-convergence bail: a higher tier (next run) may change the
            // verdict even when the tree/failure looks unchanged, so spend every run.
            // Build combined failure output for the next iteration, carrying the matching
            // full-output artifact path so the next prompt can point the agent at it.
            failingTestOutput = BuildFailureOutput(testResult, guardFailureOutput,
                bootstrapFailingResult is not null, bootstrapFailingResult?.Output);
            failingVerifyOutputPath = attemptVerifyOutputPath;
        }

        // All runs exhausted — flag.
        var finalOutcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
            $"verify failed after {maxRuns} fix-verify {(maxRuns == 1 ? "attempt" : "attempts")}", failingTestOutput, statusEntries, cancellationToken);
        return (finalOutcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
    }

    /// <summary>
    /// Snapshot the effective swival.toml content once at run start so task
    /// edits to swival.toml cannot change the profile for later stages.
    /// </summary>
    private async Task<string> ResolvePinnedSwivalProfileContentAsync(
        string rootPath, string taskDirectory, CancellationToken cancellationToken)
    {
        var pinnedProfilePath = Path.Combine(taskDirectory, "pinned-swival.toml");
        if (_options.Resume && File.Exists(pinnedProfilePath))
        {
            return await File.ReadAllTextAsync(pinnedProfilePath, cancellationToken);
        }
        var treeProfilePath = Path.Combine(rootPath, SwivalProfileSession.FileName);
        var content = File.Exists(treeProfilePath)
            ? await File.ReadAllTextAsync(treeProfilePath, cancellationToken)
            : SwivalProfileSession.DefaultToml;
        await File.WriteAllTextAsync(pinnedProfilePath, content, cancellationToken);
        return content;
    }
}
