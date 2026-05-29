using VisualRelay.Domain;

namespace VisualRelay.Core.Logging;

public interface IRelayEventSink
{
    Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default);
}

