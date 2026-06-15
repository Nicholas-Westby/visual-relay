using Avalonia.Threading;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.App.Services;

public sealed class ObservableRelayEventSink(Action<RelayEvent> onEvent) : IRelayEventSink
{
    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.Post(() => onEvent(relayEvent));
        return Task.CompletedTask;
    }
}

