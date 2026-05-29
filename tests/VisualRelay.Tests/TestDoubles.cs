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

    public void WriteConfig(string testCommand, string[] logSources)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".relay"));
        File.WriteAllText(
            Path.Combine(Root, ".relay", "config.json"),
            $$"""
            {
              "testCmd": "{{testCommand}}",
              "logSources": [{{string.Join(",", logSources.Select(s => $"\"{s}\""))}}]
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

internal sealed class ScriptedSubagentRunner : ISubagentRunner
{
    private string _codeFile = "src/app.cs";
    private string _testFile = "tests/app.tests.cs";

    public void SeedHappyPath(string codeFile, string testFile)
    {
        _codeFile = codeFile;
        _testFile = testFile;
    }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"edit files","manifest":["{{_codeFile}}","{{_testFile}}"]}""",
            5 => $$"""{"testFiles":["{{_testFile}}"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed review notes"}""",
            9 => """{"summary":"verified"}""",
            10 => """{"summary":"fixed verify"}""",
            _ => """{"summary":"ok"}"""
        };

        return Task.FromResult(new SubagentResult(
            RawText: $"```json{Environment.NewLine}{json}{Environment.NewLine}```",
            Json: json,
            IsValid: true,
            Error: null));
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

internal sealed class RecordingTaskRunner : IRelayTaskRunner
{
    public List<string> TasksRun { get; } = [];
    public Action? AfterRun { get; set; }

    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        TasksRun.Add(taskId);
        AfterRun?.Invoke();
        return Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "commit", null));
    }
}

