using VisualRelay.Domain;

namespace VisualRelay.Core.Logging;

public sealed class NullRelayEventSink : IRelayEventSink
{
    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

