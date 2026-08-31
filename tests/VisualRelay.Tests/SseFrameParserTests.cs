using System.Text;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="SseFrameParser"/> against the framing hazards measured on
/// the four providers: chunk boundaries that fall anywhere, keepalive comments
/// that must stay visible, and the <c>[DONE]</c> terminator whose absence marks
/// a truncated stream.
/// </summary>
public sealed class SseFrameParserTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A whole event in one chunk yields one event frame.</summary>
    [Fact]
    public void SingleEvent_YieldsOneFrame()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: {\"a\":1}\n\n"));

        var frame = Assert.Single(frames);
        Assert.Equal(SseFrameKind.Event, frame.Kind);
        Assert.Equal("{\"a\":1}", frame.Data);
    }

    /// <summary>
    /// A frame is delivered as soon as its blank line arrives, without waiting
    /// for the stream to end. This is what makes streaming to the UI possible.
    /// </summary>
    [Fact]
    public void FirstEvent_ArrivesWithoutBufferingWholeStream()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: first\n\n"));

        Assert.Single(frames);
        Assert.False(parser.SawDone);
    }

    /// <summary>
    /// A chunk boundary falling inside a multi-byte UTF-8 codepoint must not
    /// corrupt it: the bytes are buffered and decoded only once the line is whole.
    /// </summary>
    [Fact]
    public void EventSplitMidCodepoint_DecodesIntact()
    {
        var parser = new SseFrameParser();
        var bytes = Utf8("data: café — \U0001F600\n\n");
        // Split inside the 4-byte emoji: its first byte ends the first chunk.
        var cut = bytes.Length - 5;

        var first = parser.Append(bytes.AsSpan(0, cut));
        var second = parser.Append(bytes.AsSpan(cut));

        Assert.Empty(first);
        var frame = Assert.Single(second);
        Assert.Equal("café — \U0001F600", frame.Data);
    }

    /// <summary>The <c>[DONE]</c> sentinel is its own kind, and sets the flag.</summary>
    [Fact]
    public void DoneSentinel_IsClassifiedAsDone()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: [DONE]\n\n"));

        var frame = Assert.Single(frames);
        Assert.Equal(SseFrameKind.Done, frame.Kind);
        Assert.True(parser.SawDone);
    }

    /// <summary>
    /// A comment line is a keepalive, not an event and not silently dropped: the
    /// idle budget needs it, the output-silence clock must ignore it.
    /// </summary>
    [Fact]
    public void CommentLine_IsClassifiedAsKeepalive()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8(": ping\n"));

        var frame = Assert.Single(frames);
        Assert.Equal(SseFrameKind.Keepalive, frame.Kind);
        Assert.Equal("ping", frame.Data);
    }

    /// <summary>A keepalive between events does not disturb event accumulation.</summary>
    [Fact]
    public void KeepaliveBetweenEvents_DoesNotCorruptThem()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: one\n\n: ka\n\ndata: two\n\n"));

        Assert.Equal(
            [SseFrameKind.Event, SseFrameKind.Keepalive, SseFrameKind.Event],
            frames.Select(f => f.Kind));
        Assert.Equal(["one", "ka", "two"], frames.Select(f => f.Data));
    }

    /// <summary>Multiple data lines in one event join with a newline.</summary>
    [Fact]
    public void MultiLineData_IsConcatenatedWithNewline()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: line one\ndata: line two\n\n"));

        var frame = Assert.Single(frames);
        Assert.Equal("line one\nline two", frame.Data);
    }

    /// <summary>CRLF terminators parse identically to LF.</summary>
    [Fact]
    public void CrlfTerminators_ParseLikeLf()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: crlf\r\n\r\n"));

        var frame = Assert.Single(frames);
        Assert.Equal("crlf", frame.Data);
    }

    /// <summary>
    /// A CRLF split across two chunks must not be read as a blank line by the
    /// lone CR, which would dispatch the event a byte early.
    /// </summary>
    [Fact]
    public void CrlfSplitAcrossChunks_ParsesAsOneTerminator()
    {
        var parser = new SseFrameParser();

        var first = parser.Append(Utf8("data: split\r"));
        var second = parser.Append(Utf8("\n\r\n"));

        Assert.Empty(first);
        var frame = Assert.Single(second);
        Assert.Equal("split", frame.Data);
    }

    /// <summary>
    /// Two complete events plus a partial third in one chunk yields exactly two
    /// frames; the partial is held until its terminator arrives.
    /// </summary>
    [Fact]
    public void TwoCompleteEventsPlusPartial_YieldsTwo()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("data: a\n\ndata: b\n\ndata: c"));

        Assert.Equal(["a", "b"], frames.Select(f => f.Data));

        var rest = parser.Append(Utf8("\n\n"));

        Assert.Equal("c", Assert.Single(rest).Data);
    }

    /// <summary>An empty chunk mid-stream is valid and yields nothing.</summary>
    [Fact]
    public void EmptyChunkMidStream_YieldsNothing()
    {
        var parser = new SseFrameParser();
        parser.Append(Utf8("data: held"));

        var frames = parser.Append([]);

        Assert.Empty(frames);
        Assert.Equal("held", Assert.Single(parser.Append(Utf8("\n\n"))).Data);
    }

    /// <summary>
    /// A stream that ends without a trailing blank line still surfaces its last
    /// event on flush, so a truncated response is diagnosable rather than lost.
    /// </summary>
    [Fact]
    public void UnterminatedFinalEvent_IsFlushed()
    {
        var parser = new SseFrameParser();
        parser.Append(Utf8("data: trailing"));

        var frames = parser.Flush();

        Assert.Equal("trailing", Assert.Single(frames).Data);
    }

    /// <summary>
    /// A stream that never sent <c>[DONE]</c> leaves the flag clear, which is how
    /// the caller tells a complete stream from a truncated one.
    /// </summary>
    [Fact]
    public void StreamWithoutDone_LeavesSawDoneFalse()
    {
        var parser = new SseFrameParser();

        parser.Append(Utf8("data: a\n\n"));
        parser.Flush();

        Assert.False(parser.SawDone);
    }

    /// <summary>The <c>event:</c> field is carried through when a provider sends one.</summary>
    [Fact]
    public void EventTypeField_IsCarried()
    {
        var parser = new SseFrameParser();

        var frames = parser.Append(Utf8("event: delta\ndata: payload\n\n"));

        var frame = Assert.Single(frames);
        Assert.Equal("delta", frame.EventType);
        Assert.Equal("payload", frame.Data);
    }

    /// <summary>A byte-at-a-time feed parses identically to one whole chunk.</summary>
    [Fact]
    public void ByteAtATime_ParsesIdentically()
    {
        var parser = new SseFrameParser();
        var bytes = Utf8("data: drip\n\ndata: [DONE]\n\n");
        var kinds = new List<SseFrameKind>();

        foreach (var b in bytes)
            kinds.AddRange(parser.Append([b]).Select(f => f.Kind));

        Assert.Equal([SseFrameKind.Event, SseFrameKind.Done], kinds);
        Assert.True(parser.SawDone);
    }
}
