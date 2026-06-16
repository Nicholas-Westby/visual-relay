using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.DrainQueue;

/// <summary>
/// Console-compatible <see cref="IRelayTaskRunner"/> that mirrors
/// <c>GuiTaskRunner</c>: creates a <see cref="RelayDriver"/> with
/// <c>CreateGitCommit: true, Resume: true</c> and a per-task
/// <see cref="FileRelayEventSink"/> writing to <c>.relay/{taskId}/run.log</c>.
/// </summary>
public sealed class ConsoleTaskRunner(string mainRootPath, RelayConfig config, ITestRunner testRunner)
    : IRelayTaskRunner
{
    public Task<RelayTaskOutcome> RunTaskAsync(
        string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var consoleSink = new ConsoleRelayEventSink(taskId);
        var fileSink = new FileRelayEventSink(
            Path.Combine(mainRootPath, ".relay", taskId, "run.log"));
        var sink = new CompositeRelayEventSink(consoleSink, fileSink);
        var subagentRunner = new SwivalSubagentRunner(config, eventSink: sink);
        var deps = new RelayDriverDependencies(subagentRunner, testRunner, sink, new GitInvoker());
        var driver = new RelayDriver(deps, new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        return driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}
