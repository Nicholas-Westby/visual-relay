using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.DrainQueue;

/// <summary>
/// Thread-safe console sink that prefixes every event line with the task id
/// so output stays attributable during parallel planning.
/// </summary>
public sealed class ConsoleRelayEventSink(string taskId) : IRelayEventSink
{
    private readonly object _gate = new();

    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        var detail = relayEvent.DetailLine == relayEvent.Level
            ? string.Empty
            : $" {relayEvent.DetailLine}";

        lock (_gate)
        {
            Console.WriteLine($"[{taskId}] {relayEvent.Timestamp:HH:mm:ss} {relayEvent.DisplayLine}{detail}");
        }

        return Task.CompletedTask;
    }
}
