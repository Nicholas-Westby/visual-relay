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
}

