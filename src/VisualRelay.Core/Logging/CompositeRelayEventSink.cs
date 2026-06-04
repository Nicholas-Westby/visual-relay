using VisualRelay.Domain;

namespace VisualRelay.Core.Logging;

// Fans a single RelayEvent out to several sinks (e.g. the in-memory UI sink and
// the durable file sink) so the driver contract stays a single IRelayEventSink.
public sealed class CompositeRelayEventSink : IRelayEventSink
{
    private readonly IReadOnlyList<IRelayEventSink> _sinks;

    public CompositeRelayEventSink(params IRelayEventSink[] sinks)
    {
        _sinks = sinks;
    }

    public async Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        // One child failing must not stop the others from receiving the event.
        var tasks = _sinks.Select(sink => PublishSafelyAsync(sink, relayEvent, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private static async Task PublishSafelyAsync(IRelayEventSink sink, RelayEvent relayEvent, CancellationToken cancellationToken)
    {
        try
        {
            await sink.PublishAsync(relayEvent, cancellationToken);
        }
        catch
        {
            // Best-effort fan-out: a misbehaving sink can't break the others.
        }
    }
}
