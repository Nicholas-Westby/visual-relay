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

    // ReSharper disable once ConvertToPrimaryConstructor — _dependencies is read in
    // 22 sites across 8 partial files; a primary ctor would trigger
    // ReplaceWithPrimaryConstructorParameter and force rewriting every reference.
    // The explicit ctor keeps the field declaration cohesive with the partials.
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
            // Self-heal the VR-owned nono profile once per run (every entry point:
            // GUI Run All, headless RunTask, resume) before any sandboxed stage, so
            // the sandbox never loads a stale installed-by-name copy. The sandbox is
            // always on. A write failure throws here — the run must not silently
            // proceed unsandboxed/stale.
            await NonoProfileEnsurer.EnsureAsync(_dependencies.EnvironmentAccessor, cancellationToken);
            // Publish the command-guard middleware binary so swival can strip
            // git hook-bypass flags. Fail-open: if publish fails, swival
            // launches without --command-middleware and the squash is the floor.
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
            var firstStageToRun = 1;
            if (_options.Resume) LoadResumeState(taskDirectory, taskId, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            (previousSeal, taskHash, firstStageToRun) = await ValidateCommitGateResumeAsync(rootPath, taskDirectory, config, ledger, seals, previousSeal, taskHash, firstStageToRun, statusEntries, cancellationToken);
            var isReAdded = _options.Resume && firstStageToRun > RelayStages.All.Count && DetectReAddAndArchive(rootPath, taskId, taskDirectory, runId, input.Markdown, task?.MarkdownPath, ledger, manifest, seals, ref previousSeal, ref taskHash, ref sessionCostUsd, ref unknownCostStageCount, statusEntries, ref firstStageToRun);
            EnsureTaskInputHash(statusEntries, input.Markdown);
            IReadOnlyList<string> commitMessages = [];
            await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
            var runStartData = new Dictionary<string, string> { ["base_url"] = ModelBackend.BaseUrl, ["version"] = VersionHelper.ReadInformationalVersion() };
            if (isReAdded) runStartData["fresh"] = "prior state archived (re-added task)";
            await _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "run_start", runId, rootPath, taskId, Data: runStartData), cancellationToken);
            await WarnTestFileCmdAsync(config, runId, rootPath, taskId, cancellationToken);

            IReadOnlySet<string>? preRunUntracked = await CapturePreRunUntrackedAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken); // pre-run untracked snapshot
            var runBaseSha = await CaptureRunBaseShaAsync(rootPath, taskDirectory, forceFresh: isReAdded, cancellationToken); // HEAD at run-start, for squashing agent self-commits

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
                double? testDurationSeconds = null;

                if (stage.Kind == "driver")
                {
                    body = _options.CreateGitCommit ? "Committed by Visual Relay." : "Simulated commit by Visual Relay.";
                }
                else
                {
                    // Stage 9: run mechanical tests BEFORE the agent so it receives captured output for summarization.
                    TestRunResult? stage9TestResult = null;
                    bool stage9BootstrapFailed = false;
                    string? stage9BootstrapFailureOutput = null;
                    string? stage9BootstrapCmd = null;
                    string? stage9NewGuardOutput = null;
                    bool stage9GuardFailed = false;
                    string? stage9GuardOutput = null;
                    if (stage.Number == 9)
                    {
                        var (pre, errorHint) = await RunStage9PreAgentAsync(rootPath, runId, taskId, taskDirectory, config,
                            manifest, ledger, statusEntries, cancellationToken);
                        if (errorHint is not null)
                            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9,
                                errorHint, null, statusEntries, cancellationToken);
                        stage9TestResult = pre!.TestResult;
                        testDurationSeconds = pre.TestDurationSeconds;
                        stage9BootstrapFailed = pre.BootstrapFailed;
                        stage9BootstrapFailureOutput = pre.BootstrapFailureOutput;
                        stage9BootstrapCmd = pre.BootstrapCmd;
                        stage9NewGuardOutput = pre.NewGuardOutput;
                        stage9GuardFailed = pre.GuardFailed;
                        stage9GuardOutput = pre.GuardOutput;
                    }

                    // Stage 9 (read-only Verify) is NOT handed an imperative full-suite
                    // command: the harness already ran the suite mechanically and the
                    // agent gets the captured output via `lastTestOutput` (## Verify
                    // output). Handing it `## Verify command` ("run this exact command")
                    // re-tempted the very double-run the read-only stage exists to avoid.
                    // Only the coding stages (6/8) get the TARGETED command to self-check.
                    var testCommandForCodingStage = stage.Number is 6 or 8 ? targetedTestCommand : null;
                    var effectiveStage = implementationFrontLoaded && stage.Number == 6
                        ? stage with { Tier = "cheap", SystemPrompt = RelayStages.ConfirmImplementationSystemPrompt } : stage;
                    var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, effectiveStage, input, ledger, manifest,
                        testCommand: testCommandForCodingStage,
                        lastTestOutput: stage9TestResult?.Output,
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
                        var stage5Result = await HandleStage5Async(
                            rootPath, runId, taskId, taskDirectory, config, manifest, ledger,
                            statusEntries, json, cancellationToken);
                        if (stage5Result.Outcome is { } o)
                            return o;
                        check = stage5Result.Check;
                        testDurationSeconds = stage5Result.TestDurationSeconds;

                        // Re-check early implementation: WorktreeFilter inside
                        // HandleStage5Async may have reverted premature non-test
                        // edits back to HEAD, so the implementation is no longer
                        // in the working tree. Stage 6 should use the normal
                        // Implement prompt, not ConfirmImplementationSystemPrompt.
                        if (config.DownshiftOnEarlyImplementation)
                            implementationFrontLoaded = await EarlyImplementationDetector
                                .ImplementationAlreadyUnderwayAsync(rootPath, manifest, IsImpl, cancellationToken, isTestFile: IsTestFile);
                    }

                    if (stage.Number == 9)
                    {
                        // RED iff the test command failed OR any of bootstrap / guard /
                        // new-guard-probe failed.
                        var stage9Red = stage9TestResult!.ExitCode != 0 || stage9BootstrapFailed
                            || stage9GuardFailed || stage9NewGuardOutput is not null;
                        // The COMPLETE combined log persisted to the seed artifact: the full
                        // test output PLUS any guard/bootstrap/new-guard text — the full version
                        // of the trimmed tail the fix-verify agent receives (built once below as
                        // failingTestOutput). Null on green so the file keeps the passing output.
                        var stage9FullOutput = stage9Red
                            ? BuildFullFailureOutput(stage9TestResult, stage9GuardOutput, stage9BootstrapFailed, stage9BootstrapFailureOutput, stage9NewGuardOutput)
                            : null;
                        var stage9VerifyOutputPath = await PublishVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage, attempt: 1, config, stage9TestResult!, manifest, cancellationToken, overrideCheck: stage9Red ? "red" : "green", combinedFailureOutput: stage9FullOutput);
                        check = stage9Red ? "red" : "green";
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
                            var failingTestOutput = BuildFailureOutput(stage9TestResult, stage9GuardOutput, stage9BootstrapFailed, stage9BootstrapFailureOutput, stage9NewGuardOutput);
                            // Skip baseline diff when bootstrap, guard, or new-guard-probe is the source.
                            // NOTE: GetNewFailuresAsync runs against the LIVE rootPath (stash/restore), while
                            // testResult came from the isolated snapshot (HEAD + live overlay). These are
                            // content-equivalent, so the new-vs-baseline failure diff is valid; if the suite
                            // self-mutates, the snapshot absorbed those writes, leaving the live baseline cleaner.
                            var newFailures = (config.BaselineVerify && !stage9BootstrapFailed && !stage9GuardFailed && stage9NewGuardOutput is null)
                                ? await GetNewFailuresAsync(rootPath, taskId, runId, _dependencies.TestRunner, config.TestCommand, stage9TestResult, _dependencies.GitInvoker, cancellationToken)
                                : null;
                            if (!config.BaselineVerify || newFailures is not null || stage9BootstrapFailed || stage9GuardFailed || stage9NewGuardOutput is not null)
                            {
                                if (config.MaxVerifyLoops <= 0)
                                {
                                    var reason = newFailures is null || newFailures == "verify failed" ? "verify failed" : $"new test failures: {newFailures}";
                                    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 9, reason, failingTestOutput, statusEntries, cancellationToken);
                                }

                                // Genuinely red — record stage 9, then enter fix-verify loop.
                                (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost, stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);

                                var (loopOutcome, prevSeal, tHash, costUsd, unknownCost) = await RunVerifyFixLoopAsync(rootPath, runId, taskId, taskDirectory, config, input, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, failingTestOutput, stage9VerifyOutputPath, stage9BootstrapCmd, config.GuardCommand, pinnedSwivalProfileContent, cancellationToken);
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
                                cancellationToken, testDurationSeconds);
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
                        stopwatch, ledger, seals, statusEntries, manifest, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds);
                }
            }
            return await ExecuteCommitStageAsync(rootPath, runId, taskId, taskDirectory, config, task, commitMessages, manifest, taskHash, activeLock.Nonce, preRunUntracked, runBaseSha, statusEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            return await FlagAsync(rootPath, runId, taskId, taskDirectory, 0, $"exception: {ex.Message}", ex.ToString(), statusEntries, cancellationToken);
        }
    }
}
