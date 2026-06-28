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
/// <paramref name="environmentAccessor"/> is threaded into the driver's
/// <see cref="RelayDriverDependencies"/> so the vr-guard profile self-heal resolves
/// through it; it is <c>null</c> in production (real env, real <c>~/.config</c>).
/// </summary>
public sealed class ConsoleTaskRunner(
    string mainRootPath, RelayConfig config, ITestRunner testRunner,
    // Output-only nono-diagnostics verbosity (the global "verbose diagnostics"
    // preference), forwarded verbatim to the per-call SwivalSubagentRunner.
    bool verboseDiagnostics = false,
    IEnvironmentAccessor? environmentAccessor = null)
    : IRelayTaskRunner
{
    public Task<RelayTaskOutcome> RunTaskAsync(
        string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var consoleSink = new ConsoleRelayEventSink(taskId);
        var fileSink = new FileRelayEventSink(
            Path.Combine(mainRootPath, ".relay", taskId, "run.log"));
        var sink = new CompositeRelayEventSink(consoleSink, fileSink);
        var subagentRunner = new SwivalSubagentRunner(config, eventSink: sink, verboseDiagnostics: verboseDiagnostics);
        var deps = new RelayDriverDependencies(
            subagentRunner, testRunner, sink, new GitInvoker(), environmentAccessor);
        var driver = new RelayDriver(deps, new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        return driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}
