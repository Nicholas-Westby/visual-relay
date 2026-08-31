using System.Text;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Batches token deltas before they reach a downstream sink.
/// <para>
/// A streaming model emits deltas faster than any UI can absorb, and the GUI
/// sink posts each one to the dispatcher fire-and-forget, with no backpressure
/// at all. Publishing every delta straight through would flood that queue. This
/// accumulates contiguous deltas and releases them as one event, either when
/// enough text has built up, when enough time has passed, or as soon as anything
/// that is not a delta needs to go out — so ordering is never disturbed.
/// </para>
/// </summary>
/// <param name="inner">Where batched events go.</param>
/// <param name="flushInterval">How long text may sit unflushed. Defaults to 100 ms.</param>
/// <param name="flushChars">How much text may build up before a flush. Defaults to 200.</param>
/// <param name="timeProvider">Clock, for virtual-time tests.</param>
public sealed class CoalescingAgentEventSink(
    IAgentEventSink inner,
    TimeSpan? flushInterval = null,
    int flushChars = 200,
    TimeProvider? timeProvider = null) : IAgentEventSink
{
    private readonly TimeSpan _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly StringBuilder _pending = new();
    private readonly Lock _gate = new();

    private AgentEventKind _pendingKind;
    private int _pendingTurn;
    private DateTimeOffset _pendingSince;

    /// <inheritdoc />
    public void Publish(AgentEvent agentEvent)
    {
        if (!agentEvent.IsModelOutput)
        {
            // Flush first so a tool call never appears before the text that
            // preceded it.
            Flush();
            inner.Publish(agentEvent);
            return;
        }

        AgentEvent? release = null;
        lock (_gate)
        {
            // A change of kind or turn ends the batch: reasoning and content must
            // not be spliced together.
            if (_pending.Length > 0 && (_pendingKind != agentEvent.Kind || _pendingTurn != agentEvent.Turn))
                release = TakePending();

            if (_pending.Length == 0)
            {
                _pendingKind = agentEvent.Kind;
                _pendingTurn = agentEvent.Turn;
                _pendingSince = agentEvent.Timestamp;
            }

            _pending.Append(agentEvent.Text);

            if (release is null
                && (_pending.Length >= flushChars
                    || _timeProvider.GetUtcNow() - _pendingSince >= _flushInterval))
                release = TakePending();
        }

        if (release is not null) inner.Publish(release);
    }

    /// <summary>
    /// Releases any buffered text. Call at the end of a turn so nothing is left
    /// sitting in the buffer when the stage finishes.
    /// </summary>
    public void Flush()
    {
        AgentEvent? release;
        lock (_gate) release = TakePending();
        if (release is not null) inner.Publish(release);
    }

    private AgentEvent? TakePending()
    {
        if (_pending.Length == 0) return null;
        var text = _pending.ToString();
        _pending.Clear();
        return new AgentEvent(_pendingKind, _pendingSince, _pendingTurn, Text: text);
    }
}
