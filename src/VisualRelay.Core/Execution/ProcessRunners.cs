using System.Diagnostics;
using System.Text;
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
            var timeout = TimeSpan.FromMilliseconds(_config.SubagentTimeoutMilliseconds);

            using var watchdogCts = new CancellationTokenSource();
            using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogCts.Token);
            var processTask = ProcessCapture.RunAsync(_swivalBinary, arguments, attemptInvocation.TargetRoot, timeout, cancellationToken, killToken: watchdogCts.Token);
            var watchdogTask = FirstOutputWatchdog.WaitAsync(traceDir, firstOutputMs, watchdogCts, watchdogLinkedCts.Token);
            SubagentResult? stallResult = null;
            if (await Task.WhenAny(processTask, watchdogTask) == watchdogTask)
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

    internal List<string> BuildArguments(StageInvocation invocation)
    {
        var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
        var args = new List<string>
        {
            "-q"
        };
        if (!_config.BypassSandbox)
        {
            args.AddRange(["--sandbox", "nono", "--nono-profile", "vr-guard", "--nono-rollback"]);
        }
        args.AddRange([
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
        ]);
        return args;
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

internal static class ProcessCapture
{
    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments);
        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken);
    }

    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken, environment, killToken);
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default)
    {
        using var process = new Process();
        process.StartInfo = startInfo;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var killRegistration = killToken.CanBeCanceled
            ? killToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* already exited */ } })
            : default;

        var waitTask = process.WaitForExitAsync(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan && await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)) != waitTask)
        {
            process.Kill(entireProcessTree: true);
            return (-1, output.ToString(), true);
        }

        await waitTask;
        return (process.ExitCode, output.ToString(), false);
    }
}
