namespace VisualRelay.Domain;

/// <summary>
/// Accumulates elapsed time across one or more <em>segments</em> (attempts or
/// stages), exposing a single cumulative total that also includes the live span
/// of an in-progress segment. The total is the SUM of completed segments — a new
/// segment never discards the time already banked, so a retried/escalated unit of
/// work keeps growing instead of resetting to the current attempt.
///
/// Two consumers share it so the cumulative-across-attempts semantics stay
/// identical:
/// <list type="bullet">
///   <item>a retried stage card (e.g. Fix-verify), whose loop emits several
///   <c>stage_start</c>/<c>stage_done</c> pairs — each attempt is a segment; and</item>
///   <item>a task's overall <em>active</em> time = the sum of its stage segments,
///   excluding the idle queue-wait while another task runs (during which no
///   segment is open, so the gap is naturally excluded).</item>
/// </list>
///
/// Completed segments are banked by their <em>reported</em> duration when one is
/// available (keeping the live total consistent with the persisted per-stage
/// metric), falling back to the wall-clock between segment timestamps otherwise.
/// The sibling cumulative-turn-count work mirrors this same accumulate-don't-reset
/// shape with a plain integer sum (no live span).
/// </summary>
public sealed class CumulativeElapsed
{
    private TimeSpan _completed;
    private DateTimeOffset? _segmentStart;

    /// <summary>True while a segment is open (the current attempt/stage is live).</summary>
    public bool IsRunning => _segmentStart is not null;

    /// <summary>Time banked from completed segments (excludes any live segment).</summary>
    public TimeSpan Completed => _completed;

    /// <summary>
    /// Opens a new segment at <paramref name="startedAt"/>. Does NOT reset the
    /// banked total — a retry adds to it rather than restarting.
    /// </summary>
    public void StartSegment(DateTimeOffset startedAt) => _segmentStart = startedAt;

    /// <summary>
    /// Closes the open segment, banking its <paramref name="reportedDuration"/>
    /// (the measured/cost duration). No-op banking for non-positive durations.
    /// </summary>
    public void CompleteSegment(TimeSpan reportedDuration)
    {
        if (reportedDuration > TimeSpan.Zero)
            _completed += reportedDuration;
        _segmentStart = null;
    }

    /// <summary>
    /// Closes the open segment, banking the wall-clock from its start to
    /// <paramref name="endedAt"/>. Fallback for when no reported duration exists.
    /// </summary>
    public void CompleteSegment(DateTimeOffset endedAt)
    {
        if (_segmentStart is { } start && endedAt > start)
            _completed += endedAt - start;
        _segmentStart = null;
    }

    /// <summary>
    /// Abandons the open segment WITHOUT banking it (e.g. the attempt was flagged
    /// and produced no recorded duration). The banked total is untouched.
    /// </summary>
    public void StopSegment() => _segmentStart = null;

    /// <summary>
    /// Cumulative elapsed at <paramref name="now"/>: all banked segments plus the
    /// live span of the in-progress one (zero when nothing is open).
    /// </summary>
    public TimeSpan Total(DateTimeOffset now)
    {
        var live = _segmentStart is { } start && now > start ? now - start : TimeSpan.Zero;
        return _completed + live;
    }

    /// <summary>Clears the banked total and any open segment (a fresh run).</summary>
    public void Reset()
    {
        _completed = TimeSpan.Zero;
        _segmentStart = null;
    }
}
