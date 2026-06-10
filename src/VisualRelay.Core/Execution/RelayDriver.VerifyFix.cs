using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private static HashSet<string> ExtractFailureIds(string? output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return ids;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Trim().StartsWith("Failed ", StringComparison.Ordinal))
                ids.Add(line.Trim()["Failed ".Length..].Trim());
        return ids;
    }
    private static async Task<string?> GetNewFailuresAsync(
        string rootPath, string taskId, string runId,
        ITestRunner testRunner, string testCommand,
        TestRunResult workingResult, CancellationToken ct)
    {
        var tag = RedGate.StashTag(taskId, runId);
        var stashed = await RedGate.StashAllAsync(rootPath, tag, ct);
        try
        {
            if (!stashed) return "verify failed";
            var baseline = await testRunner.RunAsync(rootPath, testCommand, ct);
            if (baseline.TimedOut) return "verify failed";
            var current = ExtractFailureIds(workingResult.Output);
            if (current.Count == 0 && workingResult.ExitCode != 0)
                return "verify failed";
            current.ExceptWith(ExtractFailureIds(baseline.Output));
            return current.Count == 0 ? null
                : string.Join(", ", current.Order(StringComparer.Ordinal));
        }
        finally
        {
            if (stashed && await RedGate.RestoreStashAsync(rootPath, tag, ct)
                == RedGateRestoreResult.Conflict)
            {
                throw new InvalidOperationException(
                    $"Red gate restore conflict after baseline verify for tag '{tag}'.");
            }
        }
    }

    /// <summary>
    /// Runs the fix-verify loop: stage 10 → re-verify, bounded by <see cref="RelayConfig.MaxVerifyLoops"/>.
    /// Returns null outcome when the suite turns green (success). Returns a Flagged outcome when all
    /// attempts are exhausted or a non-retryable failure occurs (timeout / invalid subagent).
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
        string? bootstrapCheckCmd,
        string? guardCmd,
        CancellationToken cancellationToken)
    {
        var stage = RelayStages.All[9]; // Stage 10 — Fix-verify
        var maxLoops = config.MaxVerifyLoops;

        for (var attempt = 1; attempt <= maxLoops; attempt++)
        {
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "info", "stage_start", runId, rootPath, taskId,
                stage.Number, stage.Tier,
                Data: new Dictionary<string, string> { ["name"] = stage.Name }), cancellationToken);

            MarkStatus(statusEntries, stage.Number, "Running");
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, stage,
                input, ledger, manifest, lastTestOutput: failingTestOutput, testCommand: config.TestCommand);
            var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
            var cost = TryEstimateCost(invocation.ReportFile);
            if (cost is not null)
            {
                sessionCostUsd += cost.CostUsd;
            }
            else
            {
                unknownCostStageCount++;
            }

            if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
            {
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                    result.Error ?? "invalid subagent result", result.RawText, statusEntries, cancellationToken);
                return (outcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
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
                    rootPath, taskId, runId, _dependencies.TestRunner,
                    guardCmd, config.BaselineVerify, cancellationToken);

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

            var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
            if (testResult.TimedOut)
            {
                var outcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                    ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken);
                return (outcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
            }

            check ??= testResult.ExitCode == 0 ? "green" : "red";

            // Record attempt in ledger with labeled section.
            var header = maxLoops > 1
                ? $"## Stage {stage.Number} - {stage.Name} (attempt {attempt}/{maxLoops})"
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
            MarkStatusDone(statusEntries, stage, stopwatch.Elapsed, cost, check);
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);

            await PublishStageDoneAsync(rootPath, runId, taskId, stage, stopwatch.Elapsed, cost,
                sessionCostUsd, unknownCostStageCount, cancellationToken);

            if (check == "green")
                return (null, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);

            // Build combined failure output for next fix-verify iteration.
            failingTestOutput = BuildFailureOutput(testResult, guardFailureOutput,
                bootstrapFailingResult is not null, bootstrapFailingResult?.Output);
        }

        // All attempts exhausted — flag.
        var finalOutcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
            $"verify failed after {maxLoops} fix-verify {(maxLoops == 1 ? "attempt" : "attempts")}", failingTestOutput, statusEntries, cancellationToken);
        return (finalOutcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
    }

    private StageInvocation BuildInvocation(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        StringBuilder ledger,
        IReadOnlyList<string> manifest,
        string? lastTestOutput = null,
        string? testCommand = null)
    {
        var attempt = RelayAttempt.Next(taskDirectory, stage.Number);
        return new StageInvocation(
            stage,
            stage.Tier,
            runId,
            rootPath,
            taskId,
            input.Markdown,
            ledger.ToString(),
            manifest,
            config.LogSources,
            Path.Combine(taskDirectory, $"stage{stage.Number}-attempt{attempt}"),
            Path.Combine(taskDirectory, $"stage{stage.Number}-attempt{attempt}.report.json"),
            config.MaxTurns,
            LastTestOutput: lastTestOutput,
            TaskContext: input.Context,
            TestCommand: testCommand);
    }

    /// <summary>
    /// Records a stage's ledger entry, seal, artifacts, status, and stage_done event.
    /// Returns the updated <paramref name="previousSeal"/> and <paramref name="taskHash"/>.
    /// </summary>
    private async Task<(string PreviousSeal, string TaskHash)> RecordStageAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayStageDefinition stage,
        string body,
        string? check,
        RelayCostEstimate? cost,
        Stopwatch stopwatch,
        StringBuilder ledger,
        List<string> seals,
        List<StageStatusEntry> statusEntries,
        IReadOnlyList<string> manifest,
        string previousSeal,
        string taskHash,
        double sessionCostUsd,
        int unknownCostStageCount,
        CancellationToken cancellationToken)
    {
        AppendLedgerSection(ledger, stage, body);
        var treeHash = stage.Number >= 4 ? WorkingTreeHash(rootPath, manifest) : string.Empty;
        var artifactHash = Hashing.Sha256Hex(stage.Number.ToString(), stage.Name, body);
        var seal = Hashing.Sha256Hex(previousSeal, stage.Number.ToString(), DateTimeOffset.UtcNow.ToString("O"), artifactHash, treeHash, check ?? string.Empty);
        seals.Add(SerializeSeal(stage.Number, artifactHash, treeHash, seal, check));
        await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);
        stopwatch.Stop();
        MarkStatusDone(statusEntries, stage, stopwatch.Elapsed, cost, check);
        await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
        await PublishStageDoneAsync(rootPath, runId, taskId, stage, stopwatch.Elapsed, cost, sessionCostUsd, unknownCostStageCount, cancellationToken);
        return (seal, seal);
    }
}
