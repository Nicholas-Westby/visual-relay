using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Traces;

public sealed class RelayTraceTailer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, long> _offsets = [];
    private readonly string _traceDirectory;
    private readonly Func<TraceEntry, CancellationToken, Task>? _onEntry;
    private readonly Action? _onActivity;
    private readonly Task _loop;

    private RelayTraceTailer(string traceDirectory, Func<TraceEntry, CancellationToken, Task>? onEntry, Action? onActivity = null)
    {
        _traceDirectory = traceDirectory;
        _onEntry = onEntry;
        _onActivity = onActivity;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    public static RelayTraceTailer Start(string traceDirectory, Func<TraceEntry, CancellationToken, Task>? onEntry = null, Action? onActivity = null) =>
        new(traceDirectory, onEntry, onActivity);

    public async ValueTask DisposeAsync()
    {
        await PollAsync(CancellationToken.None);
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollAsync(cancellationToken);
            await Task.Delay(200, cancellationToken);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_traceDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_traceDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            var hadEntry = _offsets.ContainsKey(file);
            var text = await ReadNewTextAsync(file, cancellationToken);
            if (!hadEntry || text.Length > 0)
            {
                _onActivity?.Invoke();
            }
            foreach (var entry in RelayTraceParser.Parse(text))
            {
                if (_onEntry is not null)
                    await _onEntry(entry, cancellationToken);
            }
        }
    }

    private async Task<string> ReadNewTextAsync(string file, CancellationToken cancellationToken)
    {
        var offset = _offsets.GetValueOrDefault(file);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length <= offset)
        {
            return string.Empty;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        _offsets[file] = stream.Position;
        return text;
    }
}
