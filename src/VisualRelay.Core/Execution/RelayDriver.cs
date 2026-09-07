using System.Diagnostics;
using System.Text;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Init;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver : IRelayTaskRunner
{
    private readonly RelayDriverDependencies _dependencies;
    private readonly RelayDriverOptions _options;

    // ReSharper disable once ConvertToPrimaryConstructor — _dependencies is referenced
    // across 8 partials; an explicit ctor keeps the field cohesive with the partials.
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
            await NonoProfileEnsurer.EnsureAsync(_dependencies.EnvironmentAccessor, cancellationToken);
            var repository = new RelayTaskRepository(rootPath);
            var task = (await repository.ListAsync(includeNeedsReview: true, cancellationToken)).FirstOrDefault(x => x.Id == taskId);
            var input = task is null ? new RelayTaskInput(string.Empty, null) : await repository.ReadTaskInputAsync(task, cancellationToken);
            var ledger = new StringBuilder();
            var manifest = new List<string>();
            var seals = new List<string>();
            var previousSeal = string.Empty;
            var taskHash = string.Empty;
            var sessionCostUsd = 0d; var unknownCostStageCount = 0;
            var reviewPairHandled = false; string? fixSkipReason = null; var fixVerifyHandled = false;
            var implementationFrontLoaded = false;
            var firstStageToRun = 1;
            if (_options.Resume) LoadResumeState(taskDirectory, taskId, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            (previousSeal, taskHash, firstStageToRun, var commitGateOutcome) = await ValidateCommitGateResumeAsync(rootPath, taskDirectory, config, ledger, seals, previousSeal, taskHash, firstStageToRun, statusEntries, runId, taskId, cancellationToken);
            if (commitGateOutcome is not null) return commitGateOutcome;
            if (await FailIfTaskInputMissingAsync(task, input, rootPath, runId, taskId, taskDirectory, statusEntries, cancellationToken) is { } emptyInputOutcome) return emptyInputOutcome;
            var isReAdded = !_options.Resume ? DetectStaleCompletedState(rootPath, taskId, taskDirectory, runId, input.Markdown, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun) : _options.Resume && firstStageToRun > RelayStages.All.Count && DetectReAddAndArchive(rootPath, taskId, taskDirectory, runId, input.Markdown, task?.MarkdownPath, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            EnsureTaskInputHash(statusEntries, input.Markdown);
            (firstStageToRun, var flaggedOutcome) = await RestoreFlaggedWorkIfNeededAsync(rootPath, taskId, taskDirectory, firstStageToRun, ledger, statusEntries, cancellationToken);
            if (flaggedOutcome is not null) return flaggedOutcome;
            IReadOnlyList<string> commitMessages = [];
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            // No base_url: there is no single endpoint any more. Each tier resolves its
            // own provider, and the served model is recorded per stage on the report.
            var runStartData = new Dictionary<string, string> { ["version"] = VersionHelper.ReadInformationalVersion() };
            if (isReAdded) runStartData["fresh"] = "prior state archived (re-added task)";
            await _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "run_start", runId, rootPath, taskId, Data: runStartData), cancellationToken);
            await WarnTestFileCmdAsync(config, runId, rootPath, taskId, cancellationToken);
            await WarnPlaceholderTestCommandAsync(config, runId, rootPath, taskId, cancellationToken);
            RelayGitignoreWriter.EnsureWritten(rootPath);
            IReadOnlySet<string>? preRunUntracked = await CapturePreRunUntrackedAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken);
            var runBaseSha = await CaptureRunBaseShaAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken);

            foreach (var stage in RelayStages.All)
            {
                // Between stages nothing is half-written — the one clean place to stop.
                cancellationToken.ThrowIfCancellationRequested();
                // Stage 12 retires and commits with no clean point inside it, so it runs
                // on a fresh token and the cancel lands once RunTaskAsync has returned.
                var stageToken = stage.Number == 12 ? CancellationToken.None : cancellationToken;
                if (stage.Number < firstStageToRun)
                    continue;
                if (_options.LastStageToRun is { } last && stage.Number > last)
                    break;
                if (stage.Number == 11 && fixVerifyHandled)
                    continue;
                if (stage.Number == 8 && reviewPairHandled)
                    continue;
                if (stage.Number == 7)
                {
                    var pairState = await RunReviewPairAsync(rootPath, runId, taskId, taskDirectory,
                        config, input, ledger, seals, statusEntries, manifest,
                        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
                        task?.SiblingPaths ?? [], cancellationToken);
                    if (pairState.FlaggedOutcome is { } fo)
                        return fo;
                    previousSeal = pairState.PreviousSeal;
                    taskHash = pairState.TaskHash;
                    sessionCostUsd = pairState.SessionCostUsd;
                    unknownCostStageCount = pairState.UnknownCostStageCount;
                    reviewPairHandled = true; fixSkipReason = pairState.SkipReason;
                    continue;
                }
                // Skip Fix (9) when the review family left nothing to fix (see SkipStages); before stage_start so a skip never flickers Running.
                if (stage.Number == 9 && fixSkipReason is not null)
                {
                    (previousSeal, taskHash) = await RecordFixSkipAsync(rootPath, runId, taskId,
                        taskDirectory, stage, fixSkipReason, ledger, seals, statusEntries, manifest,
                        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                    continue;
                }

                await PublishAsync("info", "stage_start", rootPath, runId, taskId, stage, stageToken);
                MarkStatus(statusEntries, stage.Number, "Running");
                await WriteStatusAsync(taskDirectory, statusEntries, stageToken);
                var stopwatch = Stopwatch.StartNew();
                string body;
                string? check = null;
                RelayCostEstimate? cost = null;
                double? testDurationSeconds = null;

                if (stage.Kind == "driver")
                {
                    body = _options.CreateGitCommit ? "Committed by Visual Relay." : "Simulated commit by Visual Relay.";
                }
                else
                {
                    // Stage 10: run mechanical tests BEFORE the agent.
                    TestRunResult? stage10TestResult = null;
                    bool stage10BootstrapFailed = false; string? stage10BootstrapFailureOutput = null;
                    string? stage10BootstrapCmd = null; string? stage10NewGuardOutput = null;
                    bool stage10GuardFailed = false; string? stage10GuardOutput = null;
                    Stage10PreAgentData? stage10PreAgentData = null;
                    if (stage.Number == 10)
                    {
                        var (pre, errorHint) = await RunStage10PreAgentAsync(rootPath, runId, taskId, taskDirectory, config,
                            manifest, ledger, statusEntries, cancellationToken);
                        if (errorHint is not null)
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 10,
                                errorHint, null, statusEntries, cancellationToken);
                        stage10TestResult = pre!.TestResult;
                        testDurationSeconds = pre.TestDurationSeconds;
                        stage10BootstrapFailed = pre.BootstrapFailed;
                        stage10BootstrapFailureOutput = pre.BootstrapFailureOutput;
                        stage10BootstrapCmd = pre.BootstrapCmd;
                        stage10NewGuardOutput = pre.NewGuardOutput;
                        stage10GuardFailed = pre.GuardFailed;
                        stage10GuardOutput = pre.GuardOutput;
                        stage10PreAgentData = pre;
                    }

                    if (stage.Number == 5 && config.SkipTestsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true)
                    {
                        (previousSeal, taskHash) = await RecordTestsBypassedAsync(rootPath, runId, taskId,
                            taskDirectory, stage, ledger, seals, statusEntries, manifest, previousSeal,
                            taskHash, sessionCostUsd, unknownCostStageCount, stopwatch.Elapsed, cancellationToken);
                        continue;
                    }

                    // Stage 10 Verify gets no imperative test command; only coding stages (6/9) do.
                    var invocation = BuildStageInvocation(rootPath, runId, taskId, taskDirectory,
                        config, stage, input, ledger, manifest,
                        implementationFrontLoaded, stage10TestResult);
                    var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
                    // Fold every attempt RunAsync ran (escalation writes per-attempt reports), so
                    // the card and sessionCostUsd match the archived squash, not just attempt 1.
                    cost = EstimateStageCostCumulative(taskDirectory, stage.Number);
                    if (cost is not null) sessionCostUsd += cost.CostUsd; else unknownCostStageCount++;
                    if (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, result.Error ?? "invalid subagent result", result.RawText, statusEntries, cancellationToken,
                            cost, stopwatch.Elapsed, sessionCostUsd, unknownCostStageCount);
                    }

                    body = result.Json;
                    if (!TryParseContractJson(result.Json, out var json, out var contractError))
                    {
                        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                            contractError ?? "invalid contract JSON", result.RawText, statusEntries, cancellationToken,
                            cost, stopwatch.Elapsed, sessionCostUsd, unknownCostStageCount);
                    }
                    if (stage.Number == 4)
                    {
                        var plan = await HandleStage4Async(rootPath, runId, taskId, taskDirectory,
                            config, stage, input, ledger, manifest, json, body,
                            implementationFrontLoaded, cancellationToken);
                        body = plan.Body;
                        sessionCostUsd += plan.CostDelta;
                        unknownCostStageCount += plan.UnknownCostDelta;
                        implementationFrontLoaded = plan.ImplementationFrontLoaded;
                    }

                    if (stage.Number == 5)
                    {
                        var stage5Result = await HandleStage5Async(
                            rootPath, runId, taskId, taskDirectory, config, manifest, ledger,
                            statusEntries, json, cancellationToken);
                        if (stage5Result.Outcome is { } o)
                            return o;
                        check = stage5Result.Check;
                        testDurationSeconds = stage5Result.TestDurationSeconds;
                        implementationFrontLoaded = await RecheckEarlyImplementationAsync(
                            rootPath, config, manifest, implementationFrontLoaded,
                            cancellationToken);
                    }

                    if (stage.Number == 10)
                    {
                        var stage10Red = stage10TestResult!.ExitCode != 0 || stage10BootstrapFailed
                            || stage10GuardFailed || stage10NewGuardOutput is not null;
                        // Full output (null on green so file keeps passing output).
                        var stage10FullOutput = stage10Red
                            ? BuildFullFailureOutput(stage10TestResult, stage10GuardOutput, stage10BootstrapFailed, stage10BootstrapFailureOutput, stage10NewGuardOutput)
                            : null;
                        var (stage10VerifyOutputPath, _, _, _) = await PublishVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage, attempt: 1, config, stage10TestResult!, manifest, cancellationToken, overrideCheck: stage10Red ? "red" : "green", combinedFailureOutput: stage10FullOutput, setupChecks: SetupCheckResults.FromPreAgentData(stage10PreAgentData!, config));
                        check = stage10Red ? "red" : "green";
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
                            var failingTestOutput = BuildFailureOutput(stage10TestResult, stage10GuardOutput, stage10BootstrapFailed, stage10BootstrapFailureOutput, stage10NewGuardOutput);
                            var stage10Checks = SetupCheckResults.FromPreAgentData(stage10PreAgentData!, config);
                            // A guard that fails on the untouched base too is the machine's fault;
                            // Fix-verify would escalate its whole tier ladder over it. One that
                            // passes there was broken BY the change, and is repaired below.
                            if (await IsEnvironmentGuardFailureAsync(rootPath, runId, taskId, 10,
                                    runBaseSha, config, stage10Checks, cancellationToken))
                                return await FlagEnvironmentFailureAsync(rootPath, runId, taskId, taskDirectory, 10,
                                    stage10Checks, failingTestOutput, statusEntries, cancellationToken,
                                    cost, stopwatch.Elapsed, sessionCostUsd, unknownCostStageCount);
                            // Skip baseline diff when bootstrap/guard/new-guard-probe is the source.
                            var newFailures = (config.BaselineVerify && !stage10BootstrapFailed && !stage10GuardFailed && stage10NewGuardOutput is null)
                                ? await GetNewFailuresAsync(rootPath, taskId, runId, _dependencies.TestRunner, config.TestCommand, stage10TestResult, _dependencies.GitInvoker, cancellationToken)
                                : null;
                            if (!config.BaselineVerify || newFailures is not null || stage10BootstrapFailed || stage10GuardFailed || stage10NewGuardOutput is not null)
                            {
                                if (!config.EnableFixVerify)
                                {
                                    var reason = newFailures is null || newFailures == "verify failed" ? "verify failed" : $"new test failures: {newFailures}";
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 10, reason, stage10Checks.ToSummaryLines() + "\n\n" + failingTestOutput, statusEntries, cancellationToken,
                                        cost, stopwatch.Elapsed, sessionCostUsd, unknownCostStageCount);
                                }

                                // Genuinely red — record stage 10, enter fix-verify loop.
                                (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost, stopwatch.Elapsed, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
                                var (loopOutcome, prevSeal, tHash, costUsd, unknownCost) = await RunVerifyFixLoopAsync(rootPath, runId, taskId, taskDirectory, config, input, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, failingTestOutput, stage10VerifyOutputPath, stage10BootstrapCmd, config.GuardCommand, runBaseSha, cancellationToken);
                                if (loopOutcome is not null)
                                    return loopOutcome;
                                previousSeal = prevSeal; taskHash = tHash; sessionCostUsd = costUsd; unknownCostStageCount = unknownCost;
                                fixVerifyHandled = true;
                            }
                            else
                            {
                                check = "green"; // baseline-excluded: all failures pre-existing
                            }
                        }
                        if (check == "green" && !fixVerifyHandled)
                        {
                            // Verify green: record stage 10, then skip Fix-verify (11).
                            (previousSeal, taskHash) = await RecordVerifyGreenSkipFixVerifyAsync(
                                rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                                stopwatch.Elapsed, ledger, seals, statusEntries, manifest,
                                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
                                testDurationSeconds, cancellationToken);
                            fixVerifyHandled = true;
                        }
                    }
                }

                if (stage.Number != 12 && (stage.Number != 10 || !fixVerifyHandled) && (stage.Number != 5 || !"Skipped".Equals(statusEntries[4].Status, StringComparison.OrdinalIgnoreCase)))
                {
                    (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                        stopwatch.Elapsed, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
                }
                else if (stage.Number == 12)
                    (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                        stopwatch.Elapsed, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, stageToken, testDurationSeconds, skipStatusAndPublish: true);
            }
            // Fresh token like stage 12: a torn commit leaves neither state.
            return await ExecuteCommitStageAsync(rootPath, runId, taskId, taskDirectory, config, task, commitMessages, manifest, input.Markdown, taskHash, activeLock.Nonce, preRunUntracked, runBaseSha, statusEntries, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await WindDownCancelledRunAsync(rootPath, runId, taskId, taskDirectory, statusEntries);
        }
        catch (Exception ex)
        {
            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0, $"exception: {ex.Message}", ex.ToString(), statusEntries, cancellationToken);
        }
    }
}
