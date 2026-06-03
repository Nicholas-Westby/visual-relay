using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed class ShellTestRunner : ITestRunner
{
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var result = await ProcessCapture.RunAsync("/bin/sh", $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", rootPath, Timeout.InfiniteTimeSpan, cancellationToken);
        return new TestRunResult(result.ExitCode, result.Output);
    }
}

public sealed class SwivalSubagentRunner : ISubagentRunner
{
    private readonly RelayConfig _config;
    private readonly IRelayEventSink? _eventSink;
    private readonly string _swivalBinary;

    public SwivalSubagentRunner(RelayConfig config, string swivalBinary = "swival", IRelayEventSink? eventSink = null)
    {
        _config = config;
        _swivalBinary = swivalBinary;
        _eventSink = eventSink;
    }

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(invocation.TraceDirectory);
        await using var profileSession = await SwivalProfileSession.PrepareAsync(invocation.TargetRoot, cancellationToken);
        await using var traceTailer = _eventSink is null
            ? null
            : RelayTraceTailer.Start(invocation.TraceDirectory, (entry, token) => PublishTraceAsync(invocation, entry, token));
        var arguments = BuildArguments(invocation);
        arguments.Add(BuildPrompt(invocation));
        var timeout = TimeSpan.FromMilliseconds(_config.SubagentTimeoutMilliseconds);
        var result = await ProcessCapture.RunAsync(_swivalBinary, arguments, invocation.TargetRoot, timeout, cancellationToken);
        if (result.TimedOut)
        {
            return new SubagentResult(result.Output, null, false, $"swival timed out after {_config.SubagentTimeoutMilliseconds}ms");
        }

        if (result.ExitCode != 0)
        {
            return new SubagentResult(result.Output, null, false, $"swival exit {result.ExitCode}: {TrimForError(result.Output)}");
        }

        var json = ExtractLastFencedJson(result.Output);
        return new SubagentResult(result.Output, json, json is not null, json is null ? "no valid fenced json block" : null);
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

    private List<string> BuildArguments(StageInvocation invocation)
    {
        var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
        return new List<string>
        {
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
        };
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
        return string.Join('\n', parts);
    }

    private static string? ExtractLastFencedJson(string text)
    {
        const string marker = "```json";
        var index = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = text.IndexOf('\n', index);
        if (start < 0)
        {
            return null;
        }

        var end = FindClosingFence(text, start + 1);
        if (end < 0)
        {
            return null;
        }

        var json = text[(start + 1)..end].Trim();
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int FindClosingFence(string text, int start)
    {
        var cursor = start;
        while (cursor < text.Length)
        {
            var lineEnd = text.IndexOf('\n', cursor);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var line = text[cursor..lineEnd].Trim();
            if (line == "```")
            {
                return cursor;
            }

            cursor = lineEnd + 1;
        }

        return -1;
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
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments);
        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken);
    }

    public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunAsync(startInfo, workingDirectory, timeout, cancellationToken);
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = startInfo;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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
