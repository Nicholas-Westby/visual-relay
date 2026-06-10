using System.Diagnostics;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed class ShellTestRunner : ITestRunner
{
    private readonly TimeSpan _timeout;
    public ShellTestRunner(TimeSpan? timeout = null) => _timeout = timeout ?? Timeout.InfiniteTimeSpan;
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var result = await ProcessCapture.RunAsync("/bin/sh", $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", rootPath, _timeout, cancellationToken);
        var output = result.TimedOut
            ? $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{result.Output}"
            : result.Output;
        return new TestRunResult(result.ExitCode, output, result.TimedOut);
    }
}

public sealed class SwivalSubagentRunner : ISubagentRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // The nono capability-sandbox binary used to wrap swival when the sandbox is
    // enabled (BypassSandbox == false). nono is the WRAPPER command that runs
    // swival — `nono run <flags> -- swival <args>` — not flags passed to swival.
    private const string NonoBinary = "nono";

    // The vr-guard profile (~/.config/nono/profiles/vr-guard.json, extends the
    // registry-managed `swival` pack profile) grants broad read + network and
    // confines writes/deletes to the granted workspace. workdir.access=readwrite
    // in the swival profile means --allow-cwd grants read+write to the cwd.
    private const string NonoProfile = "vr-guard";

    private readonly RelayConfig _config;
    private readonly IRelayEventSink? _eventSink;
    private readonly string _swivalBinary;
    private readonly Func<CancellationToken, Task<BackendReadiness>> _probe;
    public SwivalSubagentRunner(
        RelayConfig config,
        string swivalBinary = "swival",
        IRelayEventSink? eventSink = null,
        Func<CancellationToken, Task<BackendReadiness>>? backendProbe = null)
    {
        _config = config;
        _swivalBinary = swivalBinary;
        _eventSink = eventSink;
        _probe = backendProbe ?? (token => BackendReadinessProbe.CheckAsync(ModelBackend.BaseUrl, ProbeTimeout, token));
    }
    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Pre-flight guard: fail fast (~1-2s) when the backend is down.
        var readiness = await _probe(cancellationToken);
        if (!readiness.IsReady)
            return new SubagentResult(string.Empty, null, false, readiness.Message);

        // Resolve the first-output threshold for this tier.
        var firstOutputMs = _config.FirstOutputTimeoutMsByTier.TryGetValue(invocation.Tier, out var tierMs)
            ? tierMs : _config.FirstOutputTimeoutMs;

        // Parse trace-dir name so retries follow stage{n}-attempt{k}.
        var traceDirParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
        RelayAttempt.TryParse(Path.GetFileName(invocation.TraceDirectory), out var stageNum, out var startAttempt);
        var maxAttempts = _config.MaxStallRetries + 1;

        for (var attempt = startAttempt; attempt < startAttempt + maxAttempts; attempt++)
        {
            var traceDir = attempt == startAttempt
                ? invocation.TraceDirectory
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}");
            var reportFile = attempt == startAttempt
                ? invocation.ReportFile
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}.report.json");
            var attemptInvocation = invocation with { TraceDirectory = traceDir, ReportFile = reportFile };

            Directory.CreateDirectory(traceDir);
            await using var profileSession = await SwivalProfileSession.PrepareAsync(attemptInvocation.TargetRoot, cancellationToken);
            await using var traceTailer = _eventSink is null ? null
                : RelayTraceTailer.Start(traceDir, (entry, token) => PublishTraceAsync(attemptInvocation, entry, token));
            var arguments = BuildArguments(attemptInvocation);
            arguments.Add(BuildPrompt(attemptInvocation));
            var (fileName, launchArguments) = BuildLaunchTarget(arguments);
            var timeout = TimeSpan.FromMilliseconds(_config.SubagentTimeoutMilliseconds);

            using var watchdogCts = new CancellationTokenSource();
            using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogCts.Token);
            var processTask = ProcessCapture.RunAsync(fileName, launchArguments, attemptInvocation.TargetRoot, timeout, cancellationToken, killToken: watchdogCts.Token);
            var watchdogTask = FirstOutputWatchdog.WaitAsync(traceDir, firstOutputMs, watchdogCts, watchdogLinkedCts.Token);
            SubagentResult? stallResult = null;
            // Task.WhenAny may return processTask when the watchdog kill triggers
            // a near-simultaneous process exit (race).  Check watchdogTask.IsCompleted
            // to catch that case so a stall is never misreported as "exit 137".
            if (await Task.WhenAny(processTask, watchdogTask) == watchdogTask
                || watchdogTask.IsCompleted)
            {
                var watchdogFired = await watchdogTask;
                if (watchdogFired)
                {
                    // Watchdog fired — no output; killToken already triggered process.Kill().
                    await processTask;
                    if (attempt < startAttempt + maxAttempts - 1)
                        continue;
                    stallResult = new SubagentResult(string.Empty, null, false,
                        ErrorHintClassifier.WithHint(
                            $"persistent model-backend stall: swival produced no output within the {firstOutputMs}ms " +
                            $"per-tier first-output threshold across {maxAttempts} attempts — " +
                            "the upstream model-backend call is likely hanging at byte 0 (pre-stream stall)."));
                }
            }

            if (stallResult is not null)
                return stallResult;

            var result = await processTask;
            watchdogCts.Cancel();
            try { await watchdogTask; } catch (OperationCanceledException) { }

            if (result.TimedOut)
            {
                var noTrace = !Directory.EnumerateFileSystemEntries(traceDir).Any();
                var noOutput = string.IsNullOrWhiteSpace(result.Output);
                var reason = noOutput && noTrace
                    ? $"swival produced no output before the {_config.SubagentTimeoutMilliseconds}ms timeout — likely a stalled model-backend call."
                    : $"swival timed out after {_config.SubagentTimeoutMilliseconds}ms. " +
                      "If swival was running a test command that hung, fix the hang and re-run only the specific " +
                      "tests you need (use a targeted subset, e.g. the TestFileCommand \"{files}\" pattern).";
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
            }

            if (result.ExitCode != 0)
            {
                var reason = $"swival exit {result.ExitCode}: {TrimForError(result.Output)}";
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
            }

            var json = FencedJsonExtractor.Extract(result.Output);
            var error = json is null ? ErrorHintClassifier.WithHint("no valid fenced json block") : null;
            return new SubagentResult(result.Output, json, json is not null, error);
        }

        return new SubagentResult(string.Empty, null, false, "unexpected: retry loop exhausted");
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

    // Pure swival arguments. The sandbox is applied by WRAPPING this whole
    // invocation in `nono run` (see BuildLaunchTarget) — never by passing
    // sandbox flags to swival (swival has no --sandbox/--nono-* flags; doing so
    // made nono print its version and exit 1, breaking every call).
    internal List<string> BuildArguments(StageInvocation invocation)
    {
        var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
        return
        [
            "-q",
            "--profile", profile,
            "--api-key", "not-needed",
            "--base-dir", invocation.TargetRoot,
            "--system-prompt", invocation.Stage.SystemPrompt,
            "--no-lifecycle",
            "--no-history",
            "--files", invocation.Stage.Files,
            "--commands", invocation.Stage.Commands,
            "--trace-dir", invocation.TraceDirectory,
            "--report", invocation.ReportFile,
            "--max-turns", invocation.MaxTurns.ToString()
        ];
    }

    // Resolve the process to actually launch. When the sandbox is bypassed we run
    // swival directly. When it is enabled we run `nono` as the wrapper:
    //
    //   nono run -p vr-guard --allow-cwd --rollback --no-rollback-prompt \
    //       -- <swivalBinary> <swivalArgs...>
    //
    // Flag rationale (TESTED with nono v0.62.0 against swival 1.0.28):
    //   run                   sandbox-and-execute a command; `--` separates nono's
    //                         flags from the wrapped program + its args.
    //   -p vr-guard           the capability profile (broad read + net; writes
    //                         confined to the workspace).
    //   --allow-cwd           grant the target repo (process cwd == TargetRoot)
    //                         without an interactive prompt. The level is set by
    //                         the profile's workdir.access (readwrite for swival),
    //                         so swival can read/write the repo and spawn the test
    //                         commands `--commands all` requires.
    //   --rollback            atomic snapshot of in-scope writes for the session.
    //   --no-rollback-prompt  skip the post-exit interactive rollback review so the
    //                         relay stays fully non-interactive (no hang on stdin).
    internal (string FileName, IReadOnlyList<string> Arguments) BuildLaunchTarget(List<string> swivalArguments)
    {
        if (_config.BypassSandbox)
        {
            return (_swivalBinary, swivalArguments);
        }

        var nonoArguments = new List<string>
        {
            "run",
            "-p", NonoProfile,
            "--allow-cwd",
            "--rollback",
            "--no-rollback-prompt",
            "--",
            _swivalBinary
        };
        nonoArguments.AddRange(swivalArguments);
        return (NonoBinary, nonoArguments);
    }

    private static string BuildPrompt(StageInvocation invocation)
    {
        var parts = new List<string>
        {
            $"# Relay stage {invocation.Stage.Number}: {invocation.Stage.Name}",
            $"Task: {invocation.TaskName}",
            string.Empty,
            "## Task input",
            invocation.TaskInput,
            string.Empty,
            "## Manifest",
            invocation.Manifest.Count > 0 ? string.Join('\n', invocation.Manifest) : "(not set yet)"
        };
        if (!string.IsNullOrWhiteSpace(invocation.TaskContext))
        {
            parts.AddRange(["", "## Task context", invocation.TaskContext]);
        }

        if (invocation.LogSources.Count > 0)
        {
            parts.AddRange(["", "## Log sources", string.Join('\n', invocation.LogSources)]);
        }

        parts.AddRange(["", "## Prior stages", invocation.LedgerSoFar, "", invocation.Stage.OutputContract]);

        if (!string.IsNullOrWhiteSpace(invocation.LastTestOutput))
        {
            parts.AddRange(["", "## Failing verify output", TrimForTrace(invocation.LastTestOutput)]);
        }

        if (!string.IsNullOrWhiteSpace(invocation.TestCommand))
        {
            parts.AddRange(["", "## Verify command", "Run this exact command to reproduce and confirm the fix:", invocation.TestCommand]);
        }

        return string.Join('\n', parts);
    }

    private static string TrimForError(string value)
    {
        var text = value.Trim();
        return text.Length <= 600 ? text : string.Concat(text.AsSpan(0, 600), "...");
    }

    private static string TrimForTrace(string value)
    {
        var text = value.Trim();
        return text.Length <= 1_500 ? text : string.Concat(text.AsSpan(0, 1_500), "...");
    }
}

