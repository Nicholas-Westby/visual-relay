using System.Text;

namespace VisualRelay.Core.Llm;

/// <summary>
/// An incremental server-sent-events parser with no transport dependency: bytes
/// are pushed in as they arrive off the wire and complete frames come out, so a
/// frame is delivered the moment its terminating blank line lands rather than
/// when the stream ends.
/// <para>
/// It buffers <em>bytes</em> and splits on line terminators before decoding.
/// A terminator is ASCII and cannot occur inside a multi-byte UTF-8 sequence, so
/// a chunk that splits a codepoint in half is reassembled correctly rather than
/// decoding to a replacement character. The in-box
/// <c>System.Net.ServerSentEvents.SseParser</c> is not used here: it consumes a
/// <see cref="Stream"/> rather than chunks, and its <c>SseItem</c> has no way to
/// represent a comment, so it drops keepalives that this consumer must observe.
/// </para>
/// </summary>
public sealed class SseFrameParser
{
    private const string DoneSentinel = "[DONE]";

    private readonly List<byte> _buffer = [];
    private readonly StringBuilder _data = new();
    private string? _eventType;
    private bool _sawData;

    /// <summary>True once the <c>[DONE]</c> terminator has been seen.</summary>
    public bool SawDone { get; private set; }

    /// <summary>
    /// Pushes the next chunk of stream bytes and returns every frame completed
    /// by it. An empty chunk is valid and yields nothing.
    /// </summary>
    /// <param name="chunk">Raw bytes exactly as received.</param>
    /// <returns>The frames completed by this chunk, in order.</returns>
    public IReadOnlyList<SseFrame> Append(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk) _buffer.Add(b);
        return DrainLines(atEnd: false);
    }

    /// <summary>
    /// Signals end of stream and returns any frame the trailing bytes complete.
    /// A provider that ends without a blank line still gets its last event.
    /// </summary>
    /// <returns>The remaining frames, in order.</returns>
    public IReadOnlyList<SseFrame> Flush()
    {
        var frames = new List<SseFrame>(DrainLines(atEnd: true));
        if (_sawData) frames.AddRange(Dispatch());
        return frames;
    }

    private List<SseFrame> DrainLines(bool atEnd)
    {
        var frames = new List<SseFrame>();
        while (true)
        {
            var newline = _buffer.IndexOf((byte)'\n');
            if (newline < 0)
            {
                // No terminator yet. At end of stream the remainder is a final
                // unterminated line; mid-stream it is a partial line to hold.
                if (!atEnd || _buffer.Count == 0) return frames;
                frames.AddRange(HandleLine(Decode(_buffer.Count)));
                _buffer.Clear();
                return frames;
            }

            // A CR immediately before the LF is part of a CRLF terminator.
            var length = newline > 0 && _buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
            var line = Decode(length);
            _buffer.RemoveRange(0, newline + 1);
            frames.AddRange(HandleLine(line));
        }
    }

    private string Decode(int length) =>
        length == 0 ? string.Empty : Encoding.UTF8.GetString(CollectionsMarshalSpan(length));

    private ReadOnlySpan<byte> CollectionsMarshalSpan(int length) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer)[..length];

    private IEnumerable<SseFrame> HandleLine(string line)
    {
        // A blank line dispatches the accumulated event.
        if (line.Length == 0)
            return _sawData ? Dispatch() : [];

        // A leading colon marks a comment. Providers send these as keepalives.
        if (line[0] == ':')
            return [new SseFrame(SseFrameKind.Keepalive, line[1..].Trim())];

        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? string.Empty : line[(colon + 1)..];
        // Exactly one leading space is stripped, per the SSE grammar.
        if (value.StartsWith(' ')) value = value[1..];

        switch (field)
        {
            case "data":
                // Multiple data lines in one event join with a newline.
                if (_sawData) _data.Append('\n');
                _data.Append(value);
                _sawData = true;
                break;
            case "event":
                _eventType = value;
                break;
            default:
                // id, retry and unknown fields are not needed by this consumer.
                break;
        }

        return [];
    }

    private IEnumerable<SseFrame> Dispatch()
    {
        var payload = _data.ToString();
        var type = _eventType;
        _data.Clear();
        _eventType = null;
        _sawData = false;

        if (payload == DoneSentinel)
        {
            SawDone = true;
            return [new SseFrame(SseFrameKind.Done, string.Empty)];
        }

        return [new SseFrame(SseFrameKind.Event, payload, type)];
    }
}
