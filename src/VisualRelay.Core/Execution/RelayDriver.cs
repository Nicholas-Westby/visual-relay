using System.Diagnostics;
using System.Text;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
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
            // Self-heal VR's nono profile once per run before any sandboxed stage.
            await NonoProfileEnsurer.EnsureAsync(_dependencies.EnvironmentAccessor, cancellationToken);
            // Publish command-guard middleware so swival can strip git hook-bypass flags.
            // Fail-open: if publish fails, swival launches without the middleware.
            _ = await CommandGuardEnsurer.EnsureAsync(rootPath, cancellationToken);
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
            var reviewPairHandled = false; var reviewFamilyClean = false;
            var fixVerifyHandled = false;
            var targetedTestCommand = BuildTargetedTestCommand(config, manifest); // updated by stage 4
            var implementationFrontLoaded = false;
            var firstStageToRun = 1;
            if (_options.Resume) LoadResumeState(taskDirectory, taskId, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            (previousSeal, taskHash, firstStageToRun) = await ValidateCommitGateResumeAsync(rootPath, taskDirectory, config, ledger, seals, previousSeal, taskHash, firstStageToRun, statusEntries, cancellationToken);
            var isReAdded = _options.Resume && firstStageToRun > RelayStages.All.Count && DetectReAddAndArchive(rootPath, taskId, taskDirectory, runId, input.Markdown, task?.MarkdownPath, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            EnsureTaskInputHash(statusEntries, input.Markdown);
            (firstStageToRun, var flaggedOutcome) = await RestoreFlaggedWorkIfNeededAsync(rootPath, taskId, taskDirectory, firstStageToRun, ledger, statusEntries, cancellationToken);
            if (flaggedOutcome is not null) return flaggedOutcome;
            IReadOnlyList<string> commitMessages = [];
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            var runStartData = new Dictionary<string, string> { ["base_url"] = ModelBackend.BaseUrl, ["version"] = VersionHelper.ReadInformationalVersion() };
            if (isReAdded) runStartData["fresh"] = "prior state archived (re-added task)";
            await _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "run_start", runId, rootPath, taskId, Data: runStartData), cancellationToken);
            await WarnTestFileCmdAsync(config, runId, rootPath, taskId, cancellationToken);
            IReadOnlySet<string>? preRunUntracked = await CapturePreRunUntrackedAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken);
            var runBaseSha = await CaptureRunBaseShaAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken);

            foreach (var stage in RelayStages.All)
            {
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
                        task?.SiblingPaths ?? [],
                        pinnedSwivalProfileContent, cancellationToken);
                    if (pairState.FlaggedOutcome is { } fo)
                        return fo;
                    previousSeal = pairState.PreviousSeal;
                    taskHash = pairState.TaskHash;
                    sessionCostUsd = pairState.SessionCostUsd;
                    unknownCostStageCount = pairState.UnknownCostStageCount;
                    reviewPairHandled = true; reviewFamilyClean = pairState.ReviewFamilyClean;
                    continue;
                }
                // Skip Fix (9) on a clean/skipped review family (see SkipStages); before stage_start so a skip never flickers Running.
                if (stage.Number == 9 && reviewFamilyClean)
                {
                    (previousSeal, taskHash) = await RecordFixSkipAsync(rootPath, runId, taskId,
                        taskDirectory, stage, ledger, seals, statusEntries, manifest,
                        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
                    continue;
                }

                await PublishAsync("info", "stage_start", rootPath, runId, taskId, stage, cancellationToken);
                MarkStatus(statusEntries, stage.Number, "Running");
                await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
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
                        MarkStatusSkipped(statusEntries, stage);
                        ledger.AppendLine("> **Skipped**: automated testing bypassed for this task.");
                        ledger.AppendLine();
                        (previousSeal, taskHash) = await RecordStageAsync(
                            rootPath, runId, taskId, taskDirectory, stage,
                            "_Skipped: automated testing bypassed for this task._",
                            "green", null, stopwatch, ledger, seals, statusEntries, manifest,
                            previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
                            cancellationToken);
                        continue;
                    }

                    // Stage 10 Verify gets no imperative test command; only coding stages (6/9) do.
                    var invocation = BuildStageInvocation(rootPath, runId, taskId, taskDirectory,
                        config, stage, input, ledger, manifest, targetedTestCommand,
                        implementationFrontLoaded, stage10TestResult, pinnedSwivalProfileContent);
                    var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
                    // Fold every attempt RunAsync ran (escalation writes per-attempt reports), so
                    // the card and sessionCostUsd match the archived squash, not just attempt 1.
                    cost = EstimateStageCostCumulative(taskDirectory, stage.Number);
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
                                .ImplementationAlreadyUnderwayAsync(rootPath, manifest, IsImpl, cancellationToken, isTestFile: f => TestPathClassifier.IsTestRelated(f, config.TestPaths));
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
                            // Skip baseline diff when bootstrap/guard/new-guard-probe is the source.
                            var newFailures = (config.BaselineVerify && !stage10BootstrapFailed && !stage10GuardFailed && stage10NewGuardOutput is null)
                                ? await GetNewFailuresAsync(rootPath, taskId, runId, _dependencies.TestRunner, config.TestCommand, stage10TestResult, _dependencies.GitInvoker, cancellationToken)
                                : null;
                            if (!config.BaselineVerify || newFailures is not null || stage10BootstrapFailed || stage10GuardFailed || stage10NewGuardOutput is not null)
                            {
                                if (!config.EnableFixVerify)
                                {
                                    var reason = newFailures is null || newFailures == "verify failed" ? "verify failed" : $"new test failures: {newFailures}";
                                    var prefix = SetupCheckResults.FromPreAgentData(stage10PreAgentData!, config).ToSummaryLines() + "\n\n";
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 10, reason, prefix + failingTestOutput, statusEntries, cancellationToken);
                                }

                                // Genuinely red — record stage 10, enter fix-verify loop.
                                (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost, stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
                                var (loopOutcome, prevSeal, tHash, costUsd, unknownCost) = await RunVerifyFixLoopAsync(rootPath, runId, taskId, taskDirectory, config, input, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, failingTestOutput, stage10VerifyOutputPath, stage10BootstrapCmd, config.GuardCommand, pinnedSwivalProfileContent, cancellationToken);
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
                                stopwatch, ledger, seals, statusEntries, manifest,
                                previousSeal, taskHash, sessionCostUsd, unknownCostStageCount,
                                testDurationSeconds, cancellationToken);
                            fixVerifyHandled = true;
                        }
                    }
                }

                if ((stage.Number != 10 || !fixVerifyHandled) && (stage.Number != 5 || !"Skipped".Equals(statusEntries[4].Status, StringComparison.OrdinalIgnoreCase)))
                {
                    (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,
                        stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
                }
            }
            return await ExecuteCommitStageAsync(rootPath, runId, taskId, taskDirectory, config, task, commitMessages, manifest, input.Markdown, taskHash, activeLock.Nonce, preRunUntracked, runBaseSha, statusEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0, $"exception: {ex.Message}", ex.ToString(), statusEntries, cancellationToken);
        }
    }
}
