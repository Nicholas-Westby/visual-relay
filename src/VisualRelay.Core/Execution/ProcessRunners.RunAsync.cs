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
        var attempt = startAttempt;
        var currentInvocation = invocation;
        string? correctivePriorOutput = null;
        string? correctiveShapeError = null;

        // Always-escalate model: ANY retryable in-process failure (contract/shape
        // reject, nonzero exit, persistent stall) goes STRAIGHT to the next tier
        // (cheap→balanced→frontier, capped) with a DOUBLED turn + ceiling budget —
        // never a same-config retry. Attempt index == run index, up to
        // MaxStageFailures runs total, then it fails. The run-1 base is the
        // (already-boost-applied) invocation budget; the doubling is suppressed in
        // flat 10× mode while the tier still escalates. Hard infra aborts (absolute
        // ceiling, socket wedge) never escalate. MaxSelfEscalations (0 for the
        // fix-verify loop, which owns escalation externally) caps this.
        var baseTurns = invocation.MaxTurns;
        var baseCeilingMs = absoluteCeilingMs;
        var flatBoost = invocation.IsTurnBoosted;
        var maxEscalations = Math.Min(Math.Max(0, _config.MaxStageFailures - 1), invocation.MaxSelfEscalations);
        var escalationCount = 0;

        // Compute nono --skip-dir basenames ONCE (target root is constant across retries).
        var skipDirs = await NonoRollbackSkipDirs.ComputeAsync(
            invocation.TargetRoot, _gitInvoker, cancellationToken);

        // One escalation rung: bump tier (capped at frontier) and scale turns +
        // ceiling (×2 per run; flat under the 10× boost), re-resolve commands for the
        // new tier, and log the transition. Returns false — the ladder is exhausted —
        // at the run cap OR when the next rung's (tier, turns) equals the current
        // config: under the flat 10× boost turn doubling is suppressed, so a
        // frontier-tier run would compute an identical config, and re-running the same
        // (tier, max-turns) is exactly what the always-escalate policy rejects. The
        // caller owns the corrective-context carry: contract failures set it before
        // escalating; stall/crash escalations clear it.
        async Task<bool> TryEscalateAsync(int currentAttempt)
        {
            if (escalationCount >= maxEscalations)
                return false;
            var fromTier = currentInvocation.Tier;
            var fromTurns = currentInvocation.MaxTurns;
            var run = escalationCount + 2;
            var toTier = StageEscalation.NextTier(fromTier);
            var toTurns = StageEscalation.TurnsForRun(baseTurns, run, flatBoost);
            // No-repeat exhaustion: never re-run an identical (tier, max-turns).
            if (toTier == fromTier && toTurns == fromTurns)
                return false;
            escalationCount++;
            currentInvocation = currentInvocation with { Tier = toTier, MaxTurns = toTurns };
            absoluteCeilingMs = StageEscalation.Scale(baseCeilingMs, StageEscalation.RunMultiplier(run, flatBoost));
            resolvedCommands = ResolveCommandsOnPath(currentInvocation.Stage.Commands, _eventSink, currentInvocation);
            await PublishEscalationAsync(currentInvocation, currentAttempt, run, maxEscalations + 1,
                fromTier, toTier, fromTurns, toTurns, cancellationToken);
            return true;
        }

        while (true)
        {
            // Recompute first-output / inactivity for current tier (may have escalated).
            var (currentFirstOutputMs, currentInactivityMs, currentOutputSilenceMs) = ResolveTierWindows(_config, currentInvocation.Tier);
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
                timeProvider: _timeProvider,
                outputSilenceTimeoutMs: currentOutputSilenceMs,
                onHeartbeat: _eventSink is null ? null : msg => _ = _eventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "debug", "watchdog_heartbeat",
                    attemptInvocation.RunId, attemptInvocation.TargetRoot,
                    attemptInvocation.TaskName, attemptInvocation.Stage.Number,
                    attemptInvocation.Tier, attempt,
                    Data: new Dictionary<string, string> { ["message"] = msg }),
                    CancellationToken.None));

            await using var activeTraceTailer = RelayTraceTailer.Start(traceDir,
                _eventSink is null ? null : (entry, token) => PublishTraceAsync(attemptInvocation, entry, token),
                onActivity: () => watchdog.Pulse("trace"), timeProvider: _timeProvider);

            var arguments = BuildPromptArguments(attemptInvocation, resolvedCommands, correctivePriorOutput, correctiveShapeError, attempt, reportFile);

            var (fileName, launchArguments) = BuildLaunchTarget(arguments, skipDirs, attemptInvocation);
            var targetEnv = BuildTargetCommandEnvironment(_config);
            var processTimeout = absoluteCeilingMs <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(absoluteCeilingMs);

            var processTask = ProcessCapture.RunAsync(fileName, launchArguments, attemptInvocation.TargetRoot,
                processTimeout, cancellationToken, environment: targetEnv.Overrides,
                envRemove: targetEnv.Remove, killToken: watchdogCts.Token,
                onActivity: watchdog.Pulse, cpuSampleIntervalMs: CpuPulseSampleIntervalMs,
                onWedgeSample: watchdog.RecordWedgeSample,
                socketProbe: BackendSocketProbe.HasEstablishedBackendConnection, timeProvider: _timeProvider);
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
                    // the absolute wall-clock ceiling, the output-silence ceiling
                    // (hung LLM requests), and the backend socket wedge.
                    if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredAbsoluteCeiling)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival timed out after {FormatCeilingMs(absoluteCeilingMs)} absolute ceiling. " +
                                $"Last signal: {wdResult.LastPulseSource}, silence: {wdResult.SilenceMs}ms."),
                            HardAbort: true,
                            Kill: new KillSignature("absolute_ceiling", wdResult.LastPulseSource, wdResult.SilenceMs, killedOutputPath));
                    }
                    else if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredOutputSilence)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival output-silence ceiling: no real output for {wdResult.SilenceMs}ms " +
                                $"while CPU pulses kept the inactivity deadline reset. Last signal: {wdResult.LastPulseSource}."),
                            HardAbort: true,
                            Kill: new KillSignature("output_silence_ceiling", wdResult.LastPulseSource, wdResult.SilenceMs, killedOutputPath));
                    }
                    else if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredSocketWedge)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival socket-wedged: the backend connection stayed ESTABLISHED but the agent " +
                                $"subtree was idle for {wdResult.SilenceMs}ms. Last signal: {wdResult.LastPulseSource}."),
                            HardAbort: true,
                            Kill: new KillSignature("socket_wedge", wdResult.LastPulseSource, wdResult.SilenceMs, killedOutputPath));
                    }
                    // Plain stall: escalate straight to the next tier (no same-config
                    // retry), carrying no corrective context. At the ladder's edge,
                    // surface the persistent-stall reason with the attempts made.
                    else if (await TryEscalateAsync(attempt))
                    {
                        correctivePriorOutput = null;
                        correctiveShapeError = null;
                        attempt++;
                        continue;
                    }
                    else
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(BuildPersistentStallReason(
                                wdResult, currentFirstOutputMs, currentInactivityMs, escalationCount + 1)),
                            Kill: new KillSignature("stall", wdResult.LastPulseSource, wdResult.SilenceMs, killedOutputPath));
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
                var reason = $"swival timed out after {FormatCeilingMs(absoluteCeilingMs)} absolute ceiling. " +
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

                // Nonzero exit: escalate straight to the next tier (no same-config
                // retry), carrying no corrective context. When the ladder is
                // exhausted, surface the real error.
                if (await TryEscalateAsync(attempt))
                {
                    correctivePriorOutput = null;
                    correctiveShapeError = null;
                    attempt++;
                    continue;
                }

                // Escalations exhausted — surface the real error.
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
                // Contract/shape reject: escalate straight to the next tier, carrying
                // the corrective diagnostic (prior output + shape error) into the
                // escalated attempt's prompt so the higher tier knows what the prior
                // output got wrong. When the ladder is exhausted, fail the stage.
                correctivePriorOutput = result.Output;
                if (await TryEscalateAsync(attempt))
                {
                    await PublishContractRetryAsync(attemptInvocation, attempt, cancellationToken);
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
