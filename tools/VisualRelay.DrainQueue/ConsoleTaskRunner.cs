using VisualRelay.Core.Configuration;
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
public sealed class ConsoleTaskRunner : IRelayTaskRunner
{
    private readonly string _mainRootPath;
    private readonly RelayConfig _config;
    private readonly ITestRunner _testRunner;

    public ConsoleTaskRunner(string mainRootPath, RelayConfig config, ITestRunner testRunner)
    {
        _mainRootPath = mainRootPath;
        _config = config;
        _testRunner = testRunner;
    }

    public Task<RelayTaskOutcome> RunTaskAsync(
        string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var consoleSink = new ConsoleRelayEventSink(taskId);
        var fileSink = new FileRelayEventSink(
            Path.Combine(_mainRootPath, ".relay", taskId, "run.log"));
        var sink = new CompositeRelayEventSink(consoleSink, fileSink);
        var subagentRunner = new SwivalSubagentRunner(_config, eventSink: sink);
        var deps = new RelayDriverDependencies(subagentRunner, _testRunner, sink);
        var driver = new RelayDriver(deps, new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        return driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}
