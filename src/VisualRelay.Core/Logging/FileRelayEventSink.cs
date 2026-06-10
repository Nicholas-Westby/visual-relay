using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Logging;

// Durable, append-only, human-readable run log. One line per event so a run can
// be `tail -f`'d live and read after the fact. Writes the FULL data values
// (never the UI's truncated DetailLine) so the failure reason is fully readable.
public sealed class FileRelayEventSink : IRelayEventSink
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileRelayEventSink(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        var line = Format(relayEvent);
        try
        {
            // Events can arrive from the trace-tailer thread and the driver at
            // the same time; serialize the append so lines never interleave.
            lock (_gate)
            {
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // A logging failure must never break a run.
        }

        return Task.CompletedTask;
    }

    private static string Format(RelayEvent relayEvent)
    {
        var scope = relayEvent.StageNumber is { } stage
            ? $"s{stage}/{relayEvent.Tier ?? "?"}"
            : "-";
        var builder = new StringBuilder();
        builder.Append(relayEvent.Timestamp.ToString("O"));
        builder.Append(' ').Append(relayEvent.Level);
        builder.Append(" run=").Append(relayEvent.RunId);
        builder.Append(" task=").Append(relayEvent.TaskId ?? "-");
        builder.Append(' ').Append(scope);
        builder.Append(' ').Append(relayEvent.EventName);
        if (relayEvent.Data is { Count: > 0 })
        {
            foreach (var pair in relayEvent.Data)
            {
                builder.Append(' ').Append(pair.Key).Append('=').Append(SingleLine(pair.Value));
            }
        }

        builder.Append(Environment.NewLine);
        return builder.ToString();
    }

    // Collapse newlines so one event is always one physical line — keeps the log
    // tail-able and line-grep-able even when a value (e.g. a trace's content or a
    // multi-line error reason) spans several lines.
    private static string SingleLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
