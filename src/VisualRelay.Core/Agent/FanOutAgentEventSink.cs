namespace VisualRelay.Core.Agent;

/// <summary>
/// Publishes each event to several sinks.
/// <para>
/// The loop takes one sink, but a stage needs two readers of the same stream:
/// the one that surfaces events to the UI, and the watchdog that measures the
/// silence between them.
/// </para>
/// </summary>
/// <param name="sinks">The sinks to publish to, in order.</param>
public sealed class FanOutAgentEventSink(params IAgentEventSink[] sinks) : IAgentEventSink
{
    /// <inheritdoc />
    public void Publish(AgentEvent agentEvent)
    {
        // One failing sink must not stop the others seeing the event, and must
        // never take the stage down: a diagnostic reader is not the work.
        foreach (var sink in sinks)
        {
            try { sink.Publish(agentEvent); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }
}
