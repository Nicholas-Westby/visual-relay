using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    // Process-tree CPU sampling cadence for the filesystem-independent
    // activity pulse (see ProcessTreeCpuSampler). Must stay well below the
    // smallest configurable inactivity window.
    private const int CpuPulseSampleIntervalMs = 4_000;

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Pre-flight: backend readiness, required tools, PATH-resolved command whitelist.
        var (preflightFailure, resolvedCommands) = await PreflightAsync(invocation, cancellationToken);
        if (preflightFailure is not null)
            return preflightFailure;

        // SubagentTimeoutMilliseconds is now an optional absolute ceiling (0 = disabled).
        var absoluteCeilingMs = invocation.AbsoluteCeilingMs > 0
            ? invocation.AbsoluteCeilingMs
            : _config.SubagentTimeoutMilliseconds;

        // Parse trace-dir name so retries follow stage{n}-attempt{k}.
        var traceDirParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
        RelayAttempt.TryParse(Path.GetFileName(invocation.TraceDirectory), out var stageNum, out var startAttempt);
        var maxStallAttempts = _config.MaxStallRetries + 1;
        var stallRetriesLeft = _config.MaxStallRetries;
        var contractRetriesLeft = _config.MaxContractRetries;
        var attempt = startAttempt;
        var currentInvocation = invocation;
        string? correctivePriorOutput = null;
        string? correctiveShapeError = null;

        // Generalized escalation: ANY in-process failure (contract/shape reject,
        // nonzero exit, persistent stall) re-runs the stage at the next tier
        // (cheap→balanced→frontier, capped) with a DOUBLED turn + ceiling budget,
        // up to MaxStageFailures runs (original + escalations) — then it fails. The
        // run-1 base is the (already-boost-applied) invocation budget; the doubling
        // is suppressed in flat 10× mode while the tier still escalates. Hard infra
        // aborts (absolute ceiling, socket wedge) never escalate. MaxSelfEscalations
        // (0 for the fix-verify loop, which owns escalation externally) caps this.
        var baseTurns = invocation.MaxTurns;
        var baseCeilingMs = absoluteCeilingMs;
        var flatBoost = invocation.IsTurnBoosted;
        var maxEscalations = Math.Min(Math.Max(0, _config.MaxStageFailures - 1), invocation.MaxSelfEscalations);
        var escalationCount = 0;

        // Compute nono --skip-dir basenames ONCE (target root is constant across retries).
        var skipDirs = await NonoRollbackSkipDirs.ComputeAsync(
            invocation.TargetRoot, _gitInvoker, cancellationToken);

        // One escalation rung: bump tier (capped at frontier), scale turns + ceiling
        // (×2 per run; flat under the 10× boost), reset the within-run stall/contract
        // budgets and corrective prompt (a higher tier re-runs fresh), re-resolve
        // commands for the new tier, and log the transition. False at the run cap.
        async Task<bool> TryEscalateAsync(int currentAttempt)
        {
            if (escalationCount >= maxEscalations)
                return false;
            var fromTier = currentInvocation.Tier;
            var fromTurns = currentInvocation.MaxTurns;
            escalationCount++;
            var run = escalationCount + 1;
            var toTier = StageEscalation.NextTier(fromTier);
            var toTurns = StageEscalation.TurnsForRun(baseTurns, run, flatBoost);
            currentInvocation = currentInvocation with { Tier = toTier, MaxTurns = toTurns };
            absoluteCeilingMs = StageEscalation.Scale(baseCeilingMs, StageEscalation.RunMultiplier(run, flatBoost));
            stallRetriesLeft = _config.MaxStallRetries;
            contractRetriesLeft = _config.MaxContractRetries;
            correctivePriorOutput = null;
            correctiveShapeError = null;
            resolvedCommands = ResolveCommandsOnPath(currentInvocation.Stage.Commands, _eventSink, currentInvocation);
            await PublishEscalationAsync(currentInvocation, currentAttempt, run, maxEscalations + 1,
                fromTier, toTier, fromTurns, toTurns, cancellationToken);
            return true;
        }

        while (true)
        {
            // Recompute first-output / inactivity for current tier (may have escalated).
            var (currentFirstOutputMs, currentInactivityMs) = ResolveTierWindows(_config, currentInvocation.Tier);
            var traceDir = attempt == startAttempt
                ? invocation.TraceDirectory
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}");
            var reportFile = attempt == startAttempt
                ? invocation.ReportFile
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}.report.json");
            var attemptInvocation = currentInvocation with { TraceDirectory = traceDir, ReportFile = reportFile };

            Directory.CreateDirectory(traceDir);
            await using var profileSession = attemptInvocation.PinnedSwivalProfileContent is not null
                ? await SwivalProfileSession.PrepareWithPinnedContentAsync(
                    attemptInvocation.TargetRoot, attemptInvocation.PinnedSwivalProfileContent,
                    attemptInvocation.RunId, attemptInvocation.TaskName,
                    _eventSink, cancellationToken)
                : await SwivalProfileSession.PrepareAsync(attemptInvocation.TargetRoot, cancellationToken);

            using var watchdogCts = new CancellationTokenSource();
            using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogCts.Token);

            // ReSharper disable once AccessToModifiedClosure — fresh watchdog+heartbeat
            // closure per iteration, fully drained (Cancel + await) before 'attempt' is
            // incremented, so the capture always sees this iteration's attempt value.
            var watchdog = new ActivityWatchdog(currentFirstOutputMs, currentInactivityMs, absoluteCeilingMs, watchdogCts,
                onHeartbeat: _eventSink is null ? null : msg => _ = _eventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "debug", "watchdog_heartbeat",
                    attemptInvocation.RunId, attemptInvocation.TargetRoot,
                    attemptInvocation.TaskName, attemptInvocation.Stage.Number,
                    attemptInvocation.Tier, attempt,
                    Data: new Dictionary<string, string> { ["message"] = msg }),
                    CancellationToken.None));

            await using var activeTraceTailer = RelayTraceTailer.Start(traceDir,
                _eventSink is null ? null : (entry, token) => PublishTraceAsync(attemptInvocation, entry, token),
                onActivity: () => watchdog.Pulse("trace"));

            var arguments = BuildPromptArguments(attemptInvocation, resolvedCommands, correctivePriorOutput, correctiveShapeError, attempt, reportFile);

            var (fileName, launchArguments) = BuildLaunchTarget(arguments, skipDirs, attemptInvocation);
            var sandboxEnv = BuildSandboxEnvironment(_config);
            var processTimeout = absoluteCeilingMs <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(absoluteCeilingMs);

            var processTask = ProcessCapture.RunAsync(fileName, launchArguments, attemptInvocation.TargetRoot,
                processTimeout, cancellationToken, environment: sandboxEnv, killToken: watchdogCts.Token,
                onActivity: watchdog.Pulse, cpuSampleIntervalMs: CpuPulseSampleIntervalMs,
                onWedgeSample: watchdog.RecordWedgeSample,
                socketProbe: BackendSocketProbe.HasEstablishedBackendConnection);
            var watchdogTask = watchdog.WaitAsync(watchdogLinkedCts.Token);
            SubagentResult? stallResult = null;
            // WhenAny may return processTask when watchdog kill triggers near-simultaneous
            // exit (race). Check watchdogCts so stall is never misreported as "exit 137".
            if (await Task.WhenAny(processTask, watchdogTask) == watchdogTask
                || watchdogCts.IsCancellationRequested)
            {
                var wdResult = await watchdogTask;
                if (wdResult.Outcome != ActivityWatchdog.Outcome.Disarmed)
                {
                    // Watchdog fired — killToken already triggered process.Kill().
                    var killedProcess = await processTask;

                    // Persist the killed attempt's captured output: it is the only
                    // autopsy evidence when trace and report never materialized.
                    var killedOutputPath = TryPersistKilledOutput(
                        traceDirParent, stageNum, attempt, wdResult,
                        currentFirstOutputMs, currentInactivityMs, killedProcess.Output);

                    await PublishStallKillAsync(attemptInvocation, attempt, wdResult,
                        currentFirstOutputMs, currentInactivityMs, killedProcess.Output.Length,
                        killedOutputPath, cancellationToken);

                    // Hard infra aborts never escalate (re-running burns the budget):
                    // the absolute wall-clock ceiling and the backend socket wedge.
                    if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredAbsoluteCeiling)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival timed out after {absoluteCeilingMs}ms absolute ceiling. " +
                                $"Last signal: {wdResult.LastPulseSource}, silence: {wdResult.SilenceMs}ms."),
                            HardAbort: true);
                    }
                    else if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredSocketWedge)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival socket-wedged: the backend connection stayed ESTABLISHED but the agent " +
                                $"subtree was idle for {wdResult.SilenceMs}ms. Last signal: {wdResult.LastPulseSource}."),
                            HardAbort: true);
                    }
                    // Plain stall: retry at the same tier within budget, then escalate.
                    else if (stallRetriesLeft > 0)
                    {
                        stallRetriesLeft--;
                        attempt++;
                        continue;
                    }
                    else if (await TryEscalateAsync(attempt))
                    {
                        attempt++;
                        continue;
                    }
                    else
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(BuildPersistentStallReason(
                                wdResult, currentFirstOutputMs, currentInactivityMs, maxStallAttempts)));
                    }
                }
            }

            if (stallResult is not null)
                return stallResult;

            var result = await processTask;
            watchdogCts.Cancel();
            try { await watchdogTask; } catch (OperationCanceledException) { }

            if (result.TimedOut)
            {
                // ProcessCapture's own timeout fired — only possible when
                // SubagentTimeoutMilliseconds > 0 (absolute ceiling backstop).
                var reason = $"swival timed out after {absoluteCeilingMs}ms absolute ceiling. " +
                    "If swival was running a test command that hung, fix the hang and re-run only the specific " +
                    "tests you need (use a targeted subset, e.g. the TestFileCommand \"{files}\" pattern).";
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason), HardAbort: true);
            }

            if (result.ExitCode != 0)
            {
                // Persist the full captured output — the real error is usually
                // at the tail (past the sandbox startup banner).  This uses
                // the same artifact name as the stall path so the autopsy
                // trail is uniform.
                var killedOutputPath = TryPersistKilledOutput(
                    traceDirParent, stageNum, attempt, $"exit_{result.ExitCode}", result.Output);

                await PublishNonzeroExitAsync(attemptInvocation, attempt, result.ExitCode,
                    result.Output.Length, killedOutputPath, cancellationToken);

                // Retry within the shared stall-retry budget (combined stall+crash
                // attempts stay bounded), then escalate tier+turns before giving up.
                if (stallRetriesLeft > 0)
                {
                    stallRetriesLeft--;
                    attempt++;
                    continue;
                }
                if (await TryEscalateAsync(attempt))
                {
                    attempt++;
                    continue;
                }

                // Retries + escalations exhausted — surface the real error.
                // BuildNonzeroExitReason distills swival's output; when that yields no
                // usable diagnostic (just the echoed prompt) it folds in the proxy
                // log's model-backend cause.
                var reason = BuildNonzeroExitReason(
                    result.ExitCode, result.Output, arguments[^1], _proxyLogReader(), killedOutputPath);
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
            }

            var json = FencedJsonExtractor.Extract(result.Output);
            correctiveShapeError = null;
            if (json is not null)
            {
                // Validate required keys from the stage contract.
                correctiveShapeError = ValidateContractShape(json, attemptInvocation.Stage.OutputContract);
                if (correctiveShapeError is not null)
                    json = null;
            }

            // Reject gitignored manifest entries at acceptance time (stages 4 & 10).
            if (json is not null && (attemptInvocation.Stage.Number == 4 || attemptInvocation.Stage.Number == 10))
            {
                correctiveShapeError = await CheckManifestAgainstGitignoreAsync(
                    json, attemptInvocation.Stage.Number, attemptInvocation.TargetRoot, cancellationToken, _gitInvoker);
                if (correctiveShapeError is not null)
                    json = null;
            }

            if (json is null)
            {
                // Within a run: nudge for the missing/rejected JSON block at the same
                // tier (corrective prompt). When that budget is spent, escalate.
                if (contractRetriesLeft > 0)
                {
                    contractRetriesLeft--;
                    correctivePriorOutput = result.Output;
                    await PublishContractRetryAsync(attemptInvocation, attempt, cancellationToken);
                    attempt++;
                    continue;
                }
                if (await TryEscalateAsync(attempt))
                {
                    attempt++;
                    continue;
                }

                return new SubagentResult(result.Output, null, false,
                    ErrorHintClassifier.WithHint("no valid fenced json block"));
            }

            return new SubagentResult(result.Output, json, true, null);
        }
    }
}
