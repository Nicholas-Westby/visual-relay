using VisualRelay.Core.Logging;

namespace VisualRelay.Core.Execution;

public sealed record RelayDriverDependencies(
    ISubagentRunner SubagentRunner,
    ITestRunner TestRunner,
    IRelayEventSink EventSink,
    IGitInvoker GitInvoker)
{
    public static RelayDriverDependencies ForTests(
        ISubagentRunner subagentRunner,
        ITestRunner testRunner,
        IRelayEventSink eventSink,
        IGitInvoker? gitInvoker = null) =>
        new(subagentRunner, testRunner, eventSink, gitInvoker ?? new GitInvoker());
}

