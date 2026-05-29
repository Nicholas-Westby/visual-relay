using VisualRelay.Core.Logging;

namespace VisualRelay.Core.Execution;

public sealed record RelayDriverDependencies(
    ISubagentRunner SubagentRunner,
    ITestRunner TestRunner,
    IRelayEventSink EventSink)
{
    public static RelayDriverDependencies ForTests(
        ISubagentRunner subagentRunner,
        ITestRunner testRunner,
        IRelayEventSink eventSink) =>
        new(subagentRunner, testRunner, eventSink);
}

