using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public interface IRelayTaskRunner
{
    Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default);
}

public interface ISubagentRunner
{
    Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default);
}

public interface ITestRunner
{
    Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="command"/> with each search-path variable in <paramref name="searchPaths"/>
    /// (a path list such as <c>PYTHONPATH</c>) put ahead of the value the command would otherwise see.
    /// A snapshot of the checkout uses it so its own copy of the project is the one imported. A runner
    /// that starts no shell of its own to set them in runs the command as it is.
    /// </summary>
    Task<TestRunResult> RunAsync(
        string rootPath, string command, IReadOnlyDictionary<string, string> searchPaths, CancellationToken cancellationToken) =>
        RunAsync(rootPath, command, cancellationToken);
}

