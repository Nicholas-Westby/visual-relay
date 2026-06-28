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
    /// Runs <paramref name="command"/> bounded by an EXPLICIT wall-clock
    /// <paramref name="hardCap"/> instead of the runner's default test timeout —
    /// used for the build phase so a long cold build honors its own (generous)
    /// budget (<see cref="VisualRelay.Domain.RelayConfig.BuildTimeoutMilliseconds"/>)
    /// rather than the shorter test budget. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// means no wall-clock cap. The default delegates to the base overload (ignoring
    /// the cap), so non-sandboxed runners and test doubles keep today's behavior; only
    /// the sandboxed runner honors the cap.
    /// </summary>
    Task<TestRunResult> RunAsync(string rootPath, string command, TimeSpan hardCap, CancellationToken cancellationToken = default) =>
        RunAsync(rootPath, command, cancellationToken);
}

