namespace VisualRelay.Tests;

/// <summary>
/// A virtual <see cref="TimeProvider"/> that freezes time at a controllable
/// epoch and advances only on demand via <see cref="Advance"/>.
/// Each <c>Advance</c> call increments the clock and synchronously fires every
/// timer whose deadline is now ≤ the new time, so <c>Task.Delay</c> /
/// <see cref="System.Threading.Timer"/> callbacks resolve inline without
/// wall-clock waiting.
///
/// Timestamp frequency is <see cref="TimeSpan.TicksPerSecond"/> so millisecond
/// arithmetic in callers remains straightforward.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;
    private readonly List<TimerEntry> _timers = new();
    private readonly List<(TimeSpan Due, TaskCompletionSource Scheduled)> _awaited = new();
    private readonly object _lock = new();

    /// <summary>
    /// Completes when a timer due <paramref name="dueTime"/> after it was set is scheduled.
    /// Code under test often sets a timer only once something real has happened, such as a
    /// child process exiting, and advancing the clock before then fires nothing; this lets a
    /// test advance exactly when that timer exists, instead of guessing with a delay.
    /// </summary>
    public Task TimerScheduledAsync(TimeSpan dueTime)
    {
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _awaited.Add((dueTime, scheduled));
        }

        return scheduled.Task;
    }

    /// <summary>The virtual "now" expressed as a UTC <see cref="DateTimeOffset"/>.</summary>
    public override DateTimeOffset GetUtcNow() =>
        new(_ticks, TimeSpan.Zero);

    /// <summary>Virtual timestamp in <see cref="TimestampFrequency"/> units.</summary>
    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Advance virtual time by <paramref name="delta"/>. Any timers whose
    /// deadline now falls at or before the new time fire synchronously
    /// (their callbacks run inline on the calling thread) before this
    /// method returns.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
            return;

        var newTicks = _ticks + delta.Ticks;

        // Snapshot pending timers under lock; fire them outside.
        List<TimerEntry> due;
        lock (_lock)
        {
            _ticks = newTicks;
            due = _timers.Where(t => t.DeadlineTicks <= newTicks).ToList();
            foreach (var t in due)
                _timers.Remove(t);
        }

        // Fire callbacks synchronously, outside the lock. Each callback
        // may re-schedule via Change(), which re-enters the lock.
        foreach (var entry in due)
            entry.Callback(entry.State);
    }

    public override ITimer CreateTimer(
        TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        var entry = new TimerEntry(callback, state, this);
        entry.Change(dueTime, period);
        return entry;
    }

    private sealed class TimerEntry(TimerCallback callback, object? state, ManualTimeProvider owner) : ITimer
    {
        public long DeadlineTicks;
        private bool _active;

        internal object? State { get; } = state;
        internal TimerCallback Callback { get; } = callback;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                _active = dueTime != Timeout.InfiniteTimeSpan;

                if (!_active)
                {
                    owner._timers.Remove(this);
                    return true;
                }

                DeadlineTicks = owner._ticks + dueTime.Ticks;

                if (!owner._timers.Contains(this))
                    owner._timers.Add(this);

                foreach (var waiter in owner._awaited.Where(w => w.Due == dueTime).ToList())
                {
                    owner._awaited.Remove(waiter);
                    waiter.Scheduled.TrySetResult();
                }

                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                _active = false;
                owner._timers.Remove(this);
            }
        }

        // ValueTask-based async disposal (required by ITimer on net10.0)
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
