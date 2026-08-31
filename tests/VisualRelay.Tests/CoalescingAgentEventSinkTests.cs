using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the batching that sits in front of the GUI sink. The dispatcher hop is
/// fire-and-forget with no backpressure, so publishing every token delta straight
/// through would flood it. Batching must never reorder anything, and must never
/// splice reasoning into content.
/// </summary>
public sealed class CoalescingAgentEventSinkTests
{
    /// <summary>Captures what reached the downstream sink.</summary>
    private sealed class Capture : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Publish(AgentEvent agentEvent) => Events.Add(agentEvent);
    }

    private static AgentEvent Delta(string text, DateTimeOffset at, int turn = 1) =>
        new(AgentEventKind.TokenDelta, at, turn, Text: text);

    /// <summary>Small deltas are held rather than forwarded one at a time.</summary>
    [Fact]
    public void SmallDeltas_AreHeld()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(capture, timeProvider: clock);

        sink.Publish(Delta("a", clock.GetUtcNow()));
        sink.Publish(Delta("b", clock.GetUtcNow()));

        Assert.Empty(capture.Events);
    }

    /// <summary>Flushing releases the buffered text as one event.</summary>
    [Fact]
    public void Flush_ReleasesOneCombinedEvent()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(capture, timeProvider: clock);

        sink.Publish(Delta("hel", clock.GetUtcNow()));
        sink.Publish(Delta("lo", clock.GetUtcNow()));
        sink.Flush();

        var released = Assert.Single(capture.Events);
        Assert.Equal("hello", released.Text);
        Assert.Equal(AgentEventKind.TokenDelta, released.Kind);
    }

    /// <summary>Enough accumulated text releases a batch without waiting.</summary>
    [Fact]
    public void EnoughText_ReleasesWithoutWaiting()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(capture, flushChars: 10, timeProvider: clock);

        sink.Publish(Delta("12345", clock.GetUtcNow()));
        sink.Publish(Delta("67890", clock.GetUtcNow()));

        Assert.Equal("1234567890", Assert.Single(capture.Events).Text);
    }

    /// <summary>Enough elapsed time releases a batch even when it is small.</summary>
    [Fact]
    public void EnoughTime_ReleasesASmallBatch()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(
            capture, flushInterval: TimeSpan.FromMilliseconds(100), timeProvider: clock);

        sink.Publish(Delta("a", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        sink.Publish(Delta("b", clock.GetUtcNow()));

        Assert.Equal("ab", Assert.Single(capture.Events).Text);
    }

    /// <summary>
    /// A non-delta event flushes first, so a tool call can never appear before
    /// the text that preceded it.
    /// </summary>
    [Fact]
    public void ANonDeltaEvent_FlushesFirstSoOrderHolds()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(capture, timeProvider: clock);

        sink.Publish(Delta("thinking about it", clock.GetUtcNow()));
        sink.Publish(new AgentEvent(
            AgentEventKind.ToolCallStarted, clock.GetUtcNow(), 1, ToolName: "read_file"));

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal(AgentEventKind.TokenDelta, capture.Events[0].Kind);
        Assert.Equal(AgentEventKind.ToolCallStarted, capture.Events[1].Kind);
    }

    /// <summary>Reasoning and content are never spliced into one event.</summary>
    [Fact]
    public void ReasoningAndContent_AreNeverSpliced()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(capture, timeProvider: clock);

        sink.Publish(new AgentEvent(AgentEventKind.ReasoningDelta, clock.GetUtcNow(), 1, Text: "why"));
        sink.Publish(Delta("answer", clock.GetUtcNow()));
        sink.Flush();

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal(AgentEventKind.ReasoningDelta, capture.Events[0].Kind);
        Assert.Equal("why", capture.Events[0].Text);
        Assert.Equal("answer", capture.Events[1].Text);
    }

    /// <summary>Deltas from different turns are never merged.</summary>
    [Fact]
    public void DeltasFromDifferentTurns_AreNeverMerged()
    {
        var capture = new Capture();
        var clock = new ManualTimeProvider();
        var sink = new CoalescingAgentEventSink(capture, timeProvider: clock);

        sink.Publish(Delta("first", clock.GetUtcNow(), turn: 1));
        sink.Publish(Delta("second", clock.GetUtcNow(), turn: 2));
        sink.Flush();

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal(1, capture.Events[0].Turn);
        Assert.Equal(2, capture.Events[1].Turn);
    }

    /// <summary>Flushing an empty buffer publishes nothing.</summary>
    [Fact]
    public void FlushingNothing_PublishesNothing()
    {
        var capture = new Capture();
        var sink = new CoalescingAgentEventSink(capture, timeProvider: new ManualTimeProvider());

        sink.Flush();
        sink.Flush();

        Assert.Empty(capture.Events);
    }

    /// <summary>
    /// Only model output is batched. That distinction is the same one the
    /// watchdog reads: a keepalive proves the connection is alive without being
    /// output, so it must not be treated as text.
    /// </summary>
    [Fact]
    public void OnlyModelOutput_IsTreatedAsText()
    {
        Assert.True(new AgentEvent(AgentEventKind.TokenDelta, DateTimeOffset.UtcNow, 1).IsModelOutput);
        Assert.True(new AgentEvent(AgentEventKind.ReasoningDelta, DateTimeOffset.UtcNow, 1).IsModelOutput);
        Assert.False(new AgentEvent(AgentEventKind.ToolCallStarted, DateTimeOffset.UtcNow, 1).IsModelOutput);
        Assert.False(new AgentEvent(AgentEventKind.Usage, DateTimeOffset.UtcNow, 1).IsModelOutput);
    }
}
