using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class CompositeRelayEventSinkTests
{
    [Fact]
    public async Task PublishAsync_ForwardsEachEventToAllChildren()
    {
        var first = new InMemoryRelayEventSink();
        var second = new InMemoryRelayEventSink();
        var composite = new CompositeRelayEventSink(first, second);

        var eventA = new RelayEvent(DateTimeOffset.UtcNow, "info", "a", "run-1", "/root");
        var eventB = new RelayEvent(DateTimeOffset.UtcNow, "error", "b", "run-1", "/root");

        await composite.PublishAsync(eventA);
        await composite.PublishAsync(eventB);

        Assert.Equal(new[] { eventA, eventB }, first.Events);
        Assert.Equal(new[] { eventA, eventB }, second.Events);
    }

    [Fact]
    public async Task PublishAsync_OneChildThrowing_StillForwardsToOthers()
    {
        var throwing = new ThrowingRelayEventSink();
        var recording = new InMemoryRelayEventSink();
        var composite = new CompositeRelayEventSink(throwing, recording);

        var relayEvent = new RelayEvent(DateTimeOffset.UtcNow, "info", "a", "run-1", "/root");

        await composite.PublishAsync(relayEvent);

        Assert.Single(recording.Events);
        Assert.Same(relayEvent, recording.Events[0]);
    }

    private sealed class ThrowingRelayEventSink : IRelayEventSink
    {
        public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("child sink failure");
    }
}
