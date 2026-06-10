using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

/// <summary>
/// Thin <see cref="IRelayTaskRunner"/> that creates a fresh driver per
/// execute call. Each call gets its own <see cref="SwivalSubagentRunner"/>
/// wired to a <see cref="CompositeRelayEventSink"/> so both driver and
/// subagent trace events land in run.log.
/// </summary>
internal sealed class GuiTaskRunner : IRelayTaskRunner
{
    private readonly string _mainRootPath;
    private readonly RelayConfig _config;
    private readonly IRelayEventSink _sharedSink;
    private readonly ITestRunner _testRunner;

    public GuiTaskRunner(string mainRootPath, RelayConfig config,
        IRelayEventSink sharedSink, ITestRunner testRunner)
    {
        _mainRootPath = mainRootPath;
        _config = config;
        _sharedSink = sharedSink;
        _testRunner = testRunner;
    }

    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var fileSink = new FileRelayEventSink(Path.Combine(_mainRootPath, ".relay", taskId, "run.log"));
        var sink = new CompositeRelayEventSink(_sharedSink, fileSink);
        var subagentRunner = new SwivalSubagentRunner(_config, eventSink: sink);
        var deps = new RelayDriverDependencies(subagentRunner, _testRunner, sink);
        var driver = new RelayDriver(deps, new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        return driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}
