using System.Diagnostics;
using System.Text.Json;
using VisualRelay.Core.Logging;
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
        // Pre-flight guard: fail fast (~1-2s) when the backend is down.
        var readiness = await _probe(cancellationToken);
        if (!readiness.IsReady)
            return new SubagentResult(string.Empty, null, false, readiness.Message);

        // Resolve whitelist against PATH so missing optional tools degrade
        // instead of crashing swival's startup preflight. Emit a
        // command_dropped event per unresolvable name.
        var resolvedCommands = ResolveCommandsOnPath(invocation.Stage.Commands, _eventSink, invocation);
        if (string.IsNullOrWhiteSpace(resolvedCommands))
        {
            return new SubagentResult(string.Empty, null, false,
                $"All whitelisted commands are missing from PATH. " +
                $"Commands: [{invocation.Stage.Commands}]. " +
                $"After dropping unresolvable names, no commands remain — refusing to run " +
                $"because swival treats an empty whitelist as unrestricted.");
        }

        // SubagentTimeoutMilliseconds is now an optional absolute ceiling (0 = disabled).
        var absoluteCeilingMs = _config.SubagentTimeoutMilliseconds;

        // Parse trace-dir name so retries follow stage{n}-attempt{k}.
        var traceDirParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
        RelayAttempt.TryParse(Path.GetFileName(invocation.TraceDirectory), out var stageNum, out var startAttempt);
        var maxStallAttempts = _config.MaxStallRetries + 1;
        var stallRetriesLeft = _config.MaxStallRetries;
        var contractRetriesLeft = _config.MaxContractRetries;
        var escalationUsed = false;
        var attempt = startAttempt;
        var currentInvocation = invocation;
        string? correctivePriorOutput = null;
        string? correctiveShapeError = null;

        while (true)
        {
            // Recompute first-output threshold for the current tier (may have escalated).
            var currentFirstOutputMs = _config.FirstOutputTimeoutMsByTier.TryGetValue(currentInvocation.Tier, out var ctMs)
                ? ctMs : _config.FirstOutputTimeoutMs;

            // Recompute inactivity timeout for the current tier (may have escalated).
            var currentInactivityMs = _config.InactivityTimeoutMsByTier?.TryGetValue(currentInvocation.Tier, out var itMs) == true
                ? itMs : _config.InactivityTimeoutMs;
            var traceDir = attempt == startAttempt
                ? invocation.TraceDirectory
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}");
            var reportFile = attempt == startAttempt
                ? invocation.ReportFile
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}.report.json");
            var attemptInvocation = currentInvocation with { TraceDirectory = traceDir, ReportFile = reportFile };

            Directory.CreateDirectory(traceDir);
            await using var profileSession = await SwivalProfileSession.PrepareAsync(attemptInvocation.TargetRoot, cancellationToken);

            using var watchdogCts = new CancellationTokenSource();
            using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogCts.Token);

            var watchdog = new ActivityWatchdog(currentFirstOutputMs, currentInactivityMs, absoluteCeilingMs, watchdogCts);

            await using var activeTraceTailer = RelayTraceTailer.Start(traceDir,
                _eventSink is null ? null : (entry, token) => PublishTraceAsync(attemptInvocation, entry, token),
                onActivity: () => watchdog.Pulse("trace"));

            var arguments = BuildArguments(attemptInvocation, resolvedCommands);
            arguments.Add(correctivePriorOutput is not null
                ? BuildCorrectivePrompt(attemptInvocation, correctivePriorOutput, correctiveShapeError)
                : BuildPrompt(attemptInvocation));
            var (fileName, launchArguments) = BuildLaunchTarget(arguments);
            var sandboxEnv = BuildSandboxEnvironment(_config);
            var processTimeout = absoluteCeilingMs <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(absoluteCeilingMs);

            var processTask = ProcessCapture.RunAsync(fileName, launchArguments, attemptInvocation.TargetRoot,
                processTimeout, cancellationToken, environment: sandboxEnv, killToken: watchdogCts.Token,
                onActivity: watchdog.Pulse, cpuSampleIntervalMs: CpuPulseSampleIntervalMs);
            var watchdogTask = watchdog.WaitAsync(watchdogLinkedCts.Token);
            SubagentResult? stallResult = null;
            // Task.WhenAny may return processTask when the watchdog kill triggers
            // a near-simultaneous process exit (race).  Check watchdogCts cancellation
            // (set synchronously by the watchdog before it returns) so a stall is
            // never misreported as "exit 137".
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

                    // Publish stall/kill event with signal details.
                    if (_eventSink is not null)
                    {
                        await _eventSink.PublishAsync(new RelayEvent(
                            DateTimeOffset.UtcNow, "warn", "stall_kill",
                            attemptInvocation.RunId, attemptInvocation.TargetRoot,
                            attemptInvocation.TaskName, attemptInvocation.Stage.Number,
                            attemptInvocation.Tier, attempt,
                            Data: new Dictionary<string, string>
                            {
                                ["reason"] = wdResult.Outcome == ActivityWatchdog.Outcome.FiredAbsoluteCeiling
                                    ? "absolute_ceiling" : "stall",
                                ["lastSignal"] = wdResult.LastPulseSource,
                                ["silenceMs"] = wdResult.SilenceMs.ToString(),
                                ["firstOutputTimeoutMs"] = currentFirstOutputMs.ToString(),
                                ["inactivityTimeoutMs"] = currentInactivityMs.ToString(),
                                ["outputBytes"] = killedProcess.Output.Length.ToString(),
                                ["outputSaved"] = killedOutputPath ?? "(persist failed)"
                            }), cancellationToken);
                    }

                    if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredAbsoluteCeiling)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival timed out after {absoluteCeilingMs}ms absolute ceiling. " +
                                $"Last signal: {wdResult.LastPulseSource}, silence: {wdResult.SilenceMs}ms."));
                    }
                    else if (stallRetriesLeft > 0)
                    {
                        stallRetriesLeft--;
                        attempt++;
                        continue;
                    }
                    else
                    {
                        var phase = wdResult.LastPulseSource == "none" ? "first-output" : "inactivity";
                        var threshold = wdResult.LastPulseSource == "none" ? currentFirstOutputMs : currentInactivityMs;
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"persistent model-backend stall: swival had no activity for " +
                                $"{wdResult.SilenceMs}ms (phase={phase}, threshold={threshold}ms). " +
                                $"Last signal: {wdResult.LastPulseSource}. " +
                                $"{maxStallAttempts} attempts exhausted."));
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
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
            }

            if (result.ExitCode != 0)
            {
                // Persist the full captured output — the real error is usually
                // at the tail (past the sandbox startup banner).  This uses
                // the same artifact name as the stall path so the autopsy
                // trail is uniform.
                var killedOutputPath = TryPersistKilledOutput(
                    traceDirParent, stageNum, attempt, $"exit_{result.ExitCode}", result.Output);

                if (_eventSink is not null)
                {
                    await _eventSink.PublishAsync(new RelayEvent(
                        DateTimeOffset.UtcNow, "warn", "nonzero_exit",
                        attemptInvocation.RunId, attemptInvocation.TargetRoot,
                        attemptInvocation.TaskName, attemptInvocation.Stage.Number,
                        attemptInvocation.Tier, attempt,
                        Data: new Dictionary<string, string>
                        {
                            ["exitCode"] = result.ExitCode.ToString(),
                            ["outputBytes"] = result.Output.Length.ToString(),
                            ["outputSaved"] = killedOutputPath ?? "(persist failed)"
                        }), cancellationToken);
                }

                // Retry within the shared stall-retry budget (combined
                // stall+crash attempts stay bounded).
                if (stallRetriesLeft > 0)
                {
                    stallRetriesLeft--;
                    attempt++;
                    continue;
                }

                // Retries exhausted — build the reason from the TAIL of the
                // output (where errors actually are), not just the head.
                var reason = $"swival exit {result.ExitCode}: {TrimForTail(result.Output)}" +
                    (killedOutputPath is not null ? $" (full output: {killedOutputPath})" : "");
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

            if (json is null)
            {
                if (contractRetriesLeft > 0)
                {
                    contractRetriesLeft--;
                    correctivePriorOutput = result.Output;
                    await PublishContractRetryAsync(attemptInvocation, attempt, cancellationToken);
                    attempt++;
                    continue;
                }

                // Try one tier escalation before giving up.
                // Only escalate when corrective retries were configured but exhausted —
                // MaxContractRetries:0 means fail-fast across the board.
                if (_config.MaxContractRetries > 0 && !escalationUsed)
                {
                    var nextTier = NextTier(currentInvocation.Tier);
                    if (nextTier is not null)
                    {
                        escalationUsed = true;
                        correctivePriorOutput = result.Output;
                        currentInvocation = currentInvocation with { Tier = nextTier };
                        // Re-resolve commands against PATH for the escalated tier's stage.
                        resolvedCommands = ResolveCommandsOnPath(currentInvocation.Stage.Commands, _eventSink, currentInvocation);
                        if (string.IsNullOrWhiteSpace(resolvedCommands))
                        {
                            return new SubagentResult(string.Empty, null, false,
                                $"All whitelisted commands are missing from PATH after tier escalation. " +
                                $"Commands: [{currentInvocation.Stage.Commands}].");
                        }
                        await PublishContractRetryAsync(attemptInvocation, attempt, cancellationToken);
                        attempt++;
                        continue;
                    }
                }

                return new SubagentResult(result.Output, null, false,
                    ErrorHintClassifier.WithHint("no valid fenced json block"));
            }

            return new SubagentResult(result.Output, json, true, null);
        }
    }

    private Task PublishTraceAsync(StageInvocation invocation, TraceEntry entry, CancellationToken cancellationToken) =>
        _eventSink!.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow,
            "info",
            "trace",
            invocation.RunId,
            invocation.TargetRoot,
            invocation.TaskName,
            invocation.Stage.Number,
            invocation.Tier,
            Data: new Dictionary<string, string>
            {
                ["kind"] = entry.Kind.ToString(),
                ["title"] = entry.Title,
                ["content"] = TrimForTrace(entry.Content)
            }), cancellationToken);
}
