using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver : IRelayTaskRunner
{
    private readonly RelayDriverDependencies _dependencies;
    private readonly RelayDriverOptions _options;
    public RelayDriver(RelayDriverDependencies dependencies, RelayDriverOptions? options = null)
    {
        _dependencies = dependencies;
        _options = options ?? RelayDriverOptions.Default;
    }

    public async Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{taskId}";
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        var statusEntries = SeedStatusEntries();
        try
        {
            var config = await RelayConfigLoader.LoadAsync(rootPath, cancellationToken);
            await using var activeLock = await ActiveTaskLock.AcquireAsync(rootPath, taskId, cancellationToken);
            Directory.CreateDirectory(taskDirectory);
            File.Delete(Path.Combine(taskDirectory, "NEEDS-REVIEW"));
            var pinnedSwivalProfileContent = await ResolvePinnedSwivalProfileContentAsync(rootPath, taskDirectory, cancellationToken);
            var repository = new RelayTaskRepository(rootPath);
            var task = (await repository.ListAsync(includeNeedsReview: true, cancellationToken)).FirstOrDefault(x => x.Id == taskId);
            var input = task is null ? new RelayTaskInput(string.Empty, null) : await repository.ReadTaskInputAsync(task, cancellationToken);
            var ledger = new StringBuilder();
            var manifest = new List<string>();
            var seals = new List<string>();
            var previousSeal = string.Empty;
            var taskHash = string.Empty;
            var sessionCostUsd = 0d;
            var unknownCostStageCount = 0;
            var firstStageToRun = 1;
            if (_options.Resume) LoadResumeState(taskDirectory, taskId, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            // Commit-gate resume validation
            (previousSeal, taskHash, firstStageToRun) = await ValidateCommitGateResumeAsync(rootPath, taskDirectory, taskId, config, ledger, seals, manifest, previousSeal, taskHash, firstStageToRun, statusEntries, cancellationToken);
            // Re-added task detection & task-input-hash stamping
            var isReAdded = _options.Resume && firstStageToRun > RelayStages.All.Count && DetectReAddAndArchive(rootPath, taskId, taskDirectory, runId, input.Markdown, task?.MarkdownPath, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            EnsureTaskInputHash(statusEntries, input.Markdown);
            IReadOnlyList<string> commitMessages = [];
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            var runStartData = new Dictionary<string, string> { ["base_url"] = ModelBackend.BaseUrl };
            if (isReAdded) runStartData["fresh"] = "prior state archived (re-added task)";
            await _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "run_start", runId, rootPath, taskId, Data: runStartData), cancellationToken);

            IReadOnlySet<string>? preRunUntracked = await CapturePreRunUntrackedAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken); // pre-run untracked snapshot

            var stage10Handled = false;
            var targetedTestCommand = BuildTargetedTestCommand(config, manifest); // updated by stage 4
            var implementationFrontLoaded = false;

            foreach (var stage in RelayStages.All)
            {
                if (stage.Number < firstStageToRun)
                    continue;
                if (_options.LastStageToRun is { } last && stage.Number > last)
                    break;
                if (stage.Number == 10 && stage10Handled)
                    continue;
                await PublishAsync("info", "stage_start", rootPath, runId, taskId, stage, cancellationToken);
                MarkStatus(statusEntries, stage.Number, "Running");
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
                var stopwatch = Stopwatch.StartNew();
                string body;
                string? check = null;
                RelayCostEstimate? cost = null;

                if (stage.Kind == "driver")
                {
                    body = _options.CreateGitCommit ? "Committed by Visual Relay." : "Simulated commit by Visual Relay.";
                }
                else
                {
                    var testCommandForCodingStage = stage.Number is 6 or 8 ? targetedTestCommand : null;
                    var effectiveStage = implementationFrontLoaded && stage.Number == 6
                        ? stage with { Tier = "cheap", SystemPrompt = RelayStages.ConfirmImplementationSystemPrompt } : stage;
                    var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, effectiveStage, input, ledger, manifest,
                        testCommand: testCommandForCodingStage,
                        pinnedSwivalProfileContent: pinnedSwivalProfileContent);
                    var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
                    cost = TryEstimateCost(invocation.ReportFile);
                    if (cost is not null) sessionCostUsd += cost.CostUsd; else unknownCostStageCount++;
                    if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, result.Error ?? "invalid subagent result", result.RawText, statusEntries, cancellationToken);
                    }

                    body = result.Json;
                    if (!TryParseContractJson(result.Json, out var json, out var contractError))
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                            contractError ?? "invalid contract JSON", result.RawText, statusEntries, cancellationToken);
                    }
                    if (stage.Number == 4)
                    {
                        manifest.Clear();
                        var raw = ReadStringArray(json, "manifest").Distinct(StringComparer.Ordinal).ToList();
                        var dropped = new List<string>();
                        var clean = new List<string>();
                        foreach (var e in raw)
                        {
                            if (IsPathUnderDirectory(rootPath, e, config.TasksDir))
                                dropped.Add(e);
                            else
                                clean.Add(e.StartsWith('+') ? e[1..] : e);
                        }
                        manifest.AddRange(clean);
                        targetedTestCommand = BuildTargetedTestCommand(config, manifest);
                        if (dropped.Count > 0)
                        {
                            var note = dropped.Count == 1
                                ? $"> **Note**: dropped 1 task-dir entry from manifest: `{dropped[0]}`"
                                : $"> **Note**: dropped {dropped.Count} task-dir entries from manifest: {string.Join(", ", dropped.Select(d => $"`{d}`"))}";
                            ledger.AppendLine(note);
                            ledger.AppendLine();
                        }
                        await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
                        (body, targetedTestCommand, var cd, var ud) = await TryPlanCompletenessRetryAsync(body, json, manifest, rootPath, runId, taskId, taskDirectory, config, stage, input, ledger, pinnedSwivalProfileContent, targetedTestCommand, cancellationToken);
                        sessionCostUsd += cd; unknownCostStageCount += ud;
                        if (config.DownshiftOnEarlyImplementation)
                            implementationFrontLoaded = await EarlyImplementationDetector
                                .ImplementationAlreadyUnderwayAsync(rootPath, manifest, IsImpl, cancellationToken, isTestFile: IsTestFile);
                    }

                    if (stage.Number == 5)
                    {
                        var testFiles = ReadStringArray(json, "testFiles");
                        var hasImpl = manifest.Any(f => !testFiles.Contains(f, StringComparer.Ordinal) && IsImpl(f));

                        if (hasImpl)
                        {
                            var command = config.TestFileCommand.Replace("{files}", string.Join(' ', testFiles), StringComparison.Ordinal);
                            var gateResult = await AuthorTestGate.RunAsync(rootPath, taskId, runId, manifest, testFiles, command, _dependencies.TestRunner, cancellationToken);
                            if (gateResult.Error is not null)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, gateResult.Error, null, statusEntries, cancellationToken);
                            }

                            if (gateResult.RestoreResult == RedGateRestoreResult.Conflict)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, "red gate stash restore conflict", null, statusEntries, cancellationToken);
                            }

                            var testResult = gateResult.TestResult;
                            if (testResult.TimedOut)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                                    ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken);
                            }

                            check = testResult.ExitCode == 0 ? "green" : "red";
                            if (check != "red")
                            {
                                if (gateResult.StashedImplementation)
                                {
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                                        "author-tests passed after implementation files were stripped", null, statusEntries, cancellationToken);
                                }

                                check = "green"; // already-resolved: no impl delta
                                ledger.AppendLine("> **Already-resolved**: no implementation delta to strip; accepted green regression coverage.");
                                ledger.AppendLine();
                            }
                        }
                    }

                    if (stage.Number == 9)
                    {
                        // ── Bootstrap smoke check (if manifest touches env-bootstrap files) ──
                        var bootstrapFailed = false;
                        string? bootstrapFailureOutput = null;
                        var (shouldRunBootstrap, bootstrapCmd) = ResolveBootstrapCheck(config, manifest);
                        if (shouldRunBootstrap)
                        {
                            var bootstrapResult = await _dependencies.TestRunner.RunAsync(rootPath, bootstrapCmd, cancellationToken);
                            if (bootstrapResult.TimedOut)
                            {
                                return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                    ErrorHintClassifier.WithHint(bootstrapResult.Output),
                                    null, statusEntries, cancellationToken);
                            }
                            if (bootstrapResult.ExitCode != 0)
                            {
                                bootstrapFailed = true;
                                bootstrapFailureOutput = bootstrapResult.Output;
                            }
                        }
                        // ── New-guard probe: run any guard scripts the task itself added ──
                        var (newGuardOutput, probeTimedOut) = await NewGuardProbeAsync(
                            rootPath, manifest, config.NewGuardPatterns, cancellationToken);
                        if (probeTimedOut)
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                ErrorHintClassifier.WithHint(newGuardOutput ?? "new guard timed out"),
                                null, statusEntries, cancellationToken);
                        // ── Repo guard check ──
                        var (guardFailed, guardOutput, guardTimedOut) = await IntegrateGuardAsync(
                            rootPath, taskId, runId, config, ledger, cancellationToken);
                        if (guardTimedOut)
                        {
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                ErrorHintClassifier.WithHint(guardOutput ?? "guard timed out"),
                                null, statusEntries, cancellationToken);
                        }

                        var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
                        if (testResult.TimedOut)
                        {
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                ErrorHintClassifier.WithHint(testResult.Output),
                                null, statusEntries, cancellationToken);
                        }
                        check = testResult.ExitCode == 0 ? "green" : "red";
                        if (bootstrapFailed || guardFailed || newGuardOutput is not null)
                            check = "red";
                        commitMessages = ReadStringArray(json, "commitMessages");
                        if (commitMessages.Count == 0)
                        {
                            var legacy = ReadOptionalString(json, "commitMessage");
                            if (legacy is not null)
                            {
                                commitMessages = [legacy];
                            }
                        }

                        if (check != "green")
                        {
                            var failingTestOutput = BuildFailureOutput(testResult, guardOutput, bootstrapFailed, bootstrapFailureOutput, newGuardOutput);
                            // Skip baseline diff when bootstrap, guard, or new-guard-probe is the source.
                            var newFailures = (config.BaselineVerify && !bootstrapFailed && !guardFailed && newGuardOutput is null)
                                ? await GetNewFailuresAsync(rootPath, taskId, runId, _dependencies.TestRunner, config.TestCommand, testResult, cancellationToken)
                                : null;
                            if (!config.BaselineVerify || newFailures is not null || bootstrapFailed || guardFailed || newGuardOutput is not null)
                            {
                                if (config.MaxVerifyLoops <= 0)
                                {
                                    var reason = newFailures is null || newFailures == "verify failed" ? "verify failed" : $"new test failures: {newFailures}";
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9, reason, failingTestOutput, statusEntries, cancellationToken);
                                }

                                // Genuinely red — record stage 9, then enter fix-verify loop.
                                (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost, stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

                                var (loopOutcome, prevSeal, tHash, costUsd, unknownCost) = await RunVerifyFixLoopAsync(rootPath, runId, taskId, taskDirectory, config, input, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, failingTestOutput, shouldRunBootstrap ? bootstrapCmd : null, config.GuardCommand, pinnedSwivalProfileContent, targetedTestCommand, cancellationToken);
                                if (loopOutcome is not null)
                                    return loopOutcome;
                                previousSeal = prevSeal; taskHash = tHash; sessionCostUsd = costUsd; unknownCostStageCount = unknownCost;
                                stage10Handled = true;
                            }
                            else
                            {
                                check = "green"; // baseline-excluded: all failures pre-existing
                            }
                        }
                        if (check == "green" && !stage10Handled)
                        {
                            // Record stage 9 green explicitly.
                            (previousSeal, taskHash) = await RecordStageAsync(
                                rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                                stopwatch, ledger, seals, statusEntries, manifest,
                                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
                                cancellationToken);
                            // Skip stage 10: nothing to fix.
                            var stage10 = RelayStages.All[9];
                            (previousSeal, taskHash) = await RecordStageAsync(
                                rootPath, runId, taskId, taskDirectory, stage10,
                                "_Skipped: Verify passed; nothing to fix._",
                                "green", null, Stopwatch.StartNew(),
                                ledger, seals, statusEntries, manifest,
                                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
                                cancellationToken);
                            stage10Handled = true;
                        }
                    }
                }

                if (stage.Number != 9 || !stage10Handled)
                {
                    (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                        stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                }
            }
            return await ExecuteCommitStageAsync(rootPath, runId, taskId, taskDirectory, config, task, commitMessages, manifest, taskHash, activeLock.Nonce, preRunUntracked, statusEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0, $"exception: {ex.Message}", ex.ToString(), statusEntries, cancellationToken);
        }
    }
}
