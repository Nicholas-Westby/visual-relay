using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

internal sealed class TestRepository : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"));

    private TestRepository()
    {
        Directory.CreateDirectory(Root);
    }

    public static TestRepository Create() => new();

    public string AttemptReportPath(string taskId, int stage, int attempt) =>
        Path.Combine(Root, ".relay", taskId, $"stage{stage}-attempt{attempt}.report.json");

    public void WriteConfig(string testCommand, string[] logSources, bool baselineVerify = true, int maxVerifyLoops = 0, bool archiveOnDone = true)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".relay"));
        File.WriteAllText(
            Path.Combine(Root, ".relay", "config.json"),
            $$"""
            {
              "testCmd": "{{testCommand}}",
              "logSources": [{{string.Join(",", logSources.Select(s => $"\"{s}\""))}}],
              "baselineVerify": {{baselineVerify.ToString().ToLowerInvariant()}},
              "maxVerifyLoops": {{maxVerifyLoops}},
              "archiveOnDone": {{archiveOnDone.ToString().ToLowerInvariant()}}
            }
            """);
    }

    public void WriteTask(string id, string markdown)
    {
        var path = Path.Combine(Root, "llm-tasks", $"{id}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown);
    }

    public void WriteNestedTask(string id, string markdown, params (string Name, string Content)[] siblings)
    {
        var dir = Path.Combine(Root, "llm-tasks", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{id}.md"), markdown);
        foreach (var sibling in siblings)
        {
            File.WriteAllText(Path.Combine(dir, sibling.Name), sibling.Content);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class InMemoryRelayEventSink : IRelayEventSink
{
    public List<RelayEvent> Events { get; } = [];

    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(relayEvent);
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedTestRunner : ITestRunner
{
    private readonly Queue<TestRunResult> _results;

    public ScriptedTestRunner(params TestRunResult[] results)
    {
        _results = new Queue<TestRunResult>(results);
    }

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new TestRunResult(0, "green"));
    }
}

/// <summary>
/// Returns a synthetic timeout result — simulating what ShellTestRunner produces
/// when the test command exceeds its cap and the process tree is killed.
/// </summary>
internal sealed class TimeoutSimulatingTestRunner : ITestRunner
{
    public const string Output =
        "test command timed out after 300000ms\n" +
        "The configured time limit was exceeded and the process was halted.\n";

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TestRunResult(-1, Output, TimedOut: true));
    }
}

internal sealed class RecordingTaskRunner : IRelayTaskRunner
{
    public List<string> TasksRun { get; } = [];
    public Action? AfterRun { get; set; }
    public Func<Task>? AfterRunAsync { get; set; }

    public async Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        TasksRun.Add(taskId);
        AfterRun?.Invoke();
        if (AfterRunAsync is not null)
        {
            await AfterRunAsync();
        }

        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "commit", null);
    }
}

internal sealed class CommitRejectingTaskRunner : IRelayTaskRunner
{
    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, "commit rejected: empty commit"));
}

internal sealed class FlaggingTaskRunner : IRelayTaskRunner
{
    private readonly string _reason;

    public FlaggingTaskRunner(string reason)
    {
        _reason = reason;
    }

    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, _reason));
}

/// <summary>
/// Returns scripted outcomes in FIFO order. When the queue is exhausted, falls
/// back to Committed so tests don't need to script every task explicitly.
/// </summary>
internal sealed class ScriptedOutcomeTaskRunner : IRelayTaskRunner
{
    private readonly Queue<RelayTaskOutcome> _outcomes;
    public List<string> TasksRun { get; } = [];

    public ScriptedOutcomeTaskRunner(params RelayTaskOutcome[] outcomes)
    {
        _outcomes = new Queue<RelayTaskOutcome>(outcomes);
    }

    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        TasksRun.Add(taskId);
        var outcome = _outcomes.Count > 0
            ? _outcomes.Dequeue()
            : new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "sha", null);
        // Preserve the caller's task id while keeping the scripted status + reason.
        return Task.FromResult(outcome with { TaskId = taskId });
    }
}

internal static class TestGit
{
    public static string Run(string rootPath, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(rootPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
