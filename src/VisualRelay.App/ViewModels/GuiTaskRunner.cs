using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

/// <summary>
/// Thin <see cref="IRelayTaskRunner"/> that creates a fresh driver per
/// execute call. Each call gets its own <see cref="SwivalSubagentRunner"/>
/// wired to a <see cref="CompositeRelayEventSink"/> so both driver and
/// subagent trace events land in run.log.
/// </summary>
internal sealed class GuiTaskRunner(
    string mainRootPath, RelayConfig config,
    IRelayEventSink sharedSink, ITestRunner testRunner) : IRelayTaskRunner
{
    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var fileSink = new FileRelayEventSink(Path.Combine(mainRootPath, ".relay", taskId, "run.log"));
        var sink = new CompositeRelayEventSink(sharedSink, fileSink);
        var subagentRunner = new SwivalSubagentRunner(config, eventSink: sink);
        var deps = new RelayDriverDependencies(subagentRunner, testRunner, sink, new GitInvoker());
        var driver = new RelayDriver(deps, new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        return driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}
