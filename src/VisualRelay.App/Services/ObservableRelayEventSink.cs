using Avalonia.Threading;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.App.Services;

public sealed class ObservableRelayEventSink : IRelayEventSink
{
    private readonly Action<RelayEvent> _onEvent;

    public ObservableRelayEventSink(Action<RelayEvent> onEvent)
    {
        _onEvent = onEvent;
    }

    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.Post(() => _onEvent(relayEvent));
        return Task.CompletedTask;
    }
}

