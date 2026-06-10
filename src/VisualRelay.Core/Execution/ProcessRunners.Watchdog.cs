using System.Diagnostics;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Tracks a sliding inactivity deadline, reset on every liveness pulse from
/// process output (stdout/stderr bytes) or trace-dir activity (new entries,
/// trace-file growth).  Two consumers:
/// (a) first-output detection – arms until the first pulse, then disarms permanently;
/// (b) ongoing inactivity deadline – resets on every pulse.
/// An optional absolute ceiling kills the stage regardless of activity.
/// </summary>
internal sealed class ActivityWatchdog
{
    public enum Outcome { Disarmed, FiredStall, FiredAbsoluteCeiling }

    public readonly record struct Result(Outcome Outcome, string LastPulseSource, long SilenceMs);

    private readonly int _firstOutputTimeoutMs;
    private readonly int _inactivityTimeoutMs;
    private readonly int _absoluteCeilingMs;
    private readonly CancellationTokenSource _kill;
    private readonly long _startTimestamp;
    private readonly object _lock = new();
    private long _lastPulseTimestamp;
    private string _lastPulseSource = "none";
    private bool _firstPulseReceived;

    public ActivityWatchdog(
        int firstOutputTimeoutMs,
        int inactivityTimeoutMs,
        int absoluteCeilingMs,
        CancellationTokenSource kill)
    {
        _firstOutputTimeoutMs = firstOutputTimeoutMs;
        _inactivityTimeoutMs = inactivityTimeoutMs;
        _absoluteCeilingMs = absoluteCeilingMs;
        _kill = kill;
        _startTimestamp = Stopwatch.GetTimestamp();
        _lastPulseTimestamp = _startTimestamp;
    }

    /// <summary>
    /// Thread-safe. Records the pulse source and timestamp.
    /// Called from ProcessCapture data-received handlers (thread-pool threads)
    /// and from the trace-tailer polling loop.
    /// </summary>
    public void Pulse(string source)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _lastPulseTimestamp = now;
            _lastPulseSource = source;
            _firstPulseReceived = true;
        }
    }

    /// <summary>
    /// Polls every 200 ms (or sooner when a deadline is imminent).
    /// Returns <see cref="Outcome.Disarmed"/> when <paramref name="ct"/> is cancelled
    /// (process exited cleanly).  Returns <see cref="Outcome.FiredStall"/> when the
    /// first-output or inactivity deadline expires.  Returns
    /// <see cref="Outcome.FiredAbsoluteCeiling"/> when the absolute wall-clock
    /// ceiling is reached (only when <c>_absoluteCeilingMs &gt; 0</c>).
    /// On any fire, <see cref="_kill"/> is cancelled before returning so
    /// ProcessCapture reacts.
    /// </summary>
    public async Task<Result> WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested)
                return new Result(Outcome.Disarmed, _lastPulseSource, 0);

            bool firedStall;
            bool firedCeiling;
            string lastSource;
            long silenceMs;
            long nowTicks;

            lock (_lock)
            {
                nowTicks = Stopwatch.GetTimestamp();
                var elapsedMs = TicksToMs(nowTicks - _startTimestamp);
                silenceMs = TicksToMs(nowTicks - _lastPulseTimestamp);
                lastSource = _lastPulseSource;

                // Check absolute ceiling first (it wins when set).
                firedCeiling = _absoluteCeilingMs > 0 && elapsedMs >= _absoluteCeilingMs;

                if (!_firstPulseReceived)
                {
                    // First-output phase: compare elapsed from start.
                    firedStall = elapsedMs >= _firstOutputTimeoutMs;
                }
                else
                {
                    // Inactivity phase: compare silence since last pulse.
                    firedStall = silenceMs >= _inactivityTimeoutMs;
                }
            }

            if (firedCeiling || firedStall)
            {
                if (!ct.IsCancellationRequested)
                    _kill.Cancel();

                var outcome = firedCeiling ? Outcome.FiredAbsoluteCeiling : Outcome.FiredStall;
                return new Result(outcome, lastSource, silenceMs);
            }

            // Compute sleep duration: cap at 200 ms, but reduce to the nearest
            // deadline so we never overshoot.
            var deadlineMs = !_firstPulseReceived
                ? _firstOutputTimeoutMs - TicksToMs(nowTicks - _startTimestamp)
                : _inactivityTimeoutMs - TicksToMs(nowTicks - _lastPulseTimestamp);

            if (_absoluteCeilingMs > 0)
            {
                var ceilingRemaining = _absoluteCeilingMs - TicksToMs(nowTicks - _startTimestamp);
                if (ceilingRemaining < deadlineMs)
                    deadlineMs = ceilingRemaining;
            }

            var delay = Math.Min(200L, Math.Max(1L, deadlineMs));
            if (delay <= 0)
                delay = 1;

            var delayTask = Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            try
            {
                await delayTask;
            }
            catch (OperationCanceledException)
            {
                return new Result(Outcome.Disarmed, _lastPulseSource, 0);
            }
        }
    }

    private static long TicksToMs(long ticks) => (long)(ticks / (double)Stopwatch.Frequency * 1000.0);
}
