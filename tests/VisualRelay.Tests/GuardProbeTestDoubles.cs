using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

internal sealed class CapturingGuardedManifestSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private readonly List<StageInvocation> _invocations = [];
    public IReadOnlyList<StageInvocation> Invocations => _invocations;
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        _invocations.Add(invocation);
        if (invocation.Stage.Number == 4)
            return Task.FromResult(new SubagentResult(
                """```json{"plan":"add guard","manifest":["tools/guards/new.sh","src/app.cs","tests/app.tests.cs"]}```""",
                """{"plan":"add guard","manifest":["tools/guards/new.sh","src/app.cs","tests/app.tests.cs"]}""",
                true, null));
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

internal sealed class RecordingDispatchTestRunner : ITestRunner
{
    private readonly List<(string RootPath, string Command)> _calls = [];
    private readonly Dictionary<string, Queue<TestRunResult>> _queues;
    public RecordingDispatchTestRunner(params (string Sentinel, TestRunResult[] Results)[] routes)
    {
        _queues = routes.ToDictionary(r => r.Sentinel, r => new Queue<TestRunResult>(r.Results));
    }
    public IReadOnlyList<(string RootPath, string Command)> Calls => _calls;
    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        _calls.Add((rootPath, command));
        foreach (var (sentinel, queue) in _queues)
            if (command.Contains(sentinel, StringComparison.Ordinal) && queue.Count > 0)
                return Task.FromResult(queue.Dequeue());
        return Task.FromResult(new TestRunResult(0, string.Empty));
    }
}
