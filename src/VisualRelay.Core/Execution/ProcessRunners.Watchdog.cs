using System.Diagnostics;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Tracks a sliding inactivity deadline, reset on every liveness pulse from
/// process output (stdout/stderr bytes) or trace-dir activity (new entries,
/// trace-file growth).  Two consumers:
/// (a) first-output detection – arms until the first pulse, then disarms permanently;
/// (b) ongoing inactivity deadline – resets on every pulse.
/// An optional absolute ceiling kills the stage regardless of activity.
///
/// Additive SOCKET-WEDGE detector (see <see cref="TryDecideSocketWedge"/>): the
/// CPU-time pulse (source "cpu") intentionally resets the inactivity deadline so a
/// genuinely-working agent whose target filesystem froze its trace view is never
/// killed.  But that same reset masks a wedged agent when CPU is accruing somewhere
/// in the process tree OTHER than the wedged agent itself.  To catch that without
/// any risk to a healthy run, the watchdog ALSO tracks silence since the last
/// *real-output* pulse (stdout/stderr/trace — NOT "cpu") and, only once that
/// real-output silence exceeds the full inactivity window, fires a stall when the
/// agent subtree is idle AND a backend socket is still ESTABLISHED.
/// </summary>
internal sealed class ActivityWatchdog
{
    public enum Outcome { Disarmed, FiredStall, FiredAbsoluteCeiling, FiredSocketWedge }

    public readonly record struct Result(Outcome Outcome, string LastPulseSource, long SilenceMs);

    /// <summary>One CPU/socket sample from <see cref="ProcessCapture"/> (the seam
    /// that knows the agent's pid). <paramref name="SubtreeIdle"/> is true when the
    /// agent process subtree accrued sub-epsilon CPU over the sample window;
    /// <paramref name="BackendSocketEstablished"/> is true when an ESTABLISHED TCP
    /// connection to the model backend exists.</summary>
    public readonly record struct WedgeSample(bool SubtreeIdle, bool BackendSocketEstablished);

    private readonly int _firstOutputTimeoutMs;
    private readonly int _inactivityTimeoutMs;
    private readonly int _absoluteCeilingMs;
    private readonly CancellationTokenSource _kill;
    private readonly Action<string>? _onHeartbeat;
    private readonly long _startTimestamp;
    private readonly object _lock = new();
    private long _lastPulseTimestamp;
    private string _lastPulseSource = "none";
    private bool _firstPulseReceived;
    private long _lastHeartbeatTimestamp;

    // Silence-since-last-REAL-output clock. Unlike _lastPulseTimestamp it is NOT
    // reset by the "cpu" pulse, so it measures genuine stdout/stderr/trace silence
    // even while the CPU pulse keeps the main deadline alive. Seeded at start; the
    // first real-output pulse re-seeds it.
    private long _lastRealOutputTimestamp;

    // Latest wedge sample from ProcessCapture (pid-scoped CPU + backend socket).
    // Defaults to "not idle, no socket" so the wedge path is inert until the first
    // real sample arrives — a missing sampler can never trigger a kill.
    private WedgeSample _lastWedgeSample = new(SubtreeIdle: false, BackendSocketEstablished: false);

    // Emit a diagnostic heartbeat line at most once per this interval so the
    // watchdog's internal view of pulse history is visible in run.log.
    private const long HeartbeatIntervalMs = 60_000;

    public ActivityWatchdog(
        int firstOutputTimeoutMs,
        int inactivityTimeoutMs,
        int absoluteCeilingMs,
        CancellationTokenSource kill,
        Action<string>? onHeartbeat = null)
    {
        _firstOutputTimeoutMs = firstOutputTimeoutMs;
        _inactivityTimeoutMs = inactivityTimeoutMs;
        _absoluteCeilingMs = absoluteCeilingMs;
        _kill = kill;
        _onHeartbeat = onHeartbeat;
        _startTimestamp = Stopwatch.GetTimestamp();
        _lastPulseTimestamp = _startTimestamp;
        _lastRealOutputTimestamp = _startTimestamp;
        _lastHeartbeatTimestamp = _startTimestamp;
    }

    /// <summary>
    /// Thread-safe. Records the pulse source and timestamp.
    /// Called from ProcessCapture data-received handlers (thread-pool threads)
    /// and from the trace-tailer polling loop. The "cpu" source resets the main
    /// inactivity deadline (so a fs-frozen but busy agent survives) but NOT the
    /// real-output silence clock the socket-wedge detector reads.
    /// </summary>
    public void Pulse(string source)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _lastPulseTimestamp = now;
            _lastPulseSource = source;
            _firstPulseReceived = true;
            if (!string.Equals(source, "cpu", StringComparison.Ordinal))
                _lastRealOutputTimestamp = now;
        }
    }

    /// <summary>
    /// Thread-safe. Records the latest CPU-subtree-idle + backend-socket sample
    /// from ProcessCapture so the wedge detector reads a fresh verdict in its loop.
    /// </summary>
    public void RecordWedgeSample(WedgeSample sample)
    {
        lock (_lock)
        {
            _lastWedgeSample = sample;
        }
    }

    /// <summary>
    /// Pure socket-wedge decision, exposed for hermetic unit testing of the exact
    /// production gate. Fires ONLY when all three hold: (1) the agent has produced
    /// no real output (stdout/stderr/trace) for at least the full inactivity window
    /// — cpu pulses do not reset this clock; (2) the agent process subtree is idle
    /// (sub-epsilon CPU over the last sample window); (3) a backend socket is still
    /// ESTABLISHED. This is strictly additive: it can only fire inside a sustained
    /// silence the inactivity timeout already covers, and a healthy agent (recent
    /// output, OR a busy subtree, OR no open backend socket) fails at least one gate.
    /// </summary>
    internal static bool TryDecideSocketWedge(
        bool firstPulseReceived,
        long realOutputSilenceMs,
        int inactivityTimeoutMs,
        WedgeSample sample) =>
        firstPulseReceived
        && realOutputSilenceMs >= inactivityTimeoutMs
        && sample is { SubtreeIdle: true, BackendSocketEstablished: true };

    /// <summary>
    /// Polls every 200 ms (or sooner when a deadline is imminent).
    /// Returns <see cref="Outcome.Disarmed"/> when <paramref name="ct"/> is cancelled
    /// (process exited cleanly).  Returns <see cref="Outcome.FiredStall"/> when the
    /// first-output or inactivity deadline expires, <see cref="Outcome.FiredSocketWedge"/>
    /// when the additive socket-wedge gate trips, or
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
            bool firedWedge;
            string lastSource;
            long silenceMs;
            long nowTicks;
            long heartbeatDeadlineMs;

            lock (_lock)
            {
                nowTicks = Stopwatch.GetTimestamp();
                var elapsedMs = TicksToMs(nowTicks - _startTimestamp);
                silenceMs = TicksToMs(nowTicks - _lastPulseTimestamp);
                var realOutputSilenceMs = TicksToMs(nowTicks - _lastRealOutputTimestamp);
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

                // Additive: a cpu-pulse-masked wedge (real output silent past the
                // inactivity window, agent subtree idle, backend socket ESTABLISHED).
                firedWedge = TryDecideSocketWedge(
                    _firstPulseReceived, realOutputSilenceMs, _inactivityTimeoutMs, _lastWedgeSample);

                // Snapshot the deadline for heartbeat logging (outside lock).
                heartbeatDeadlineMs = !_firstPulseReceived
                    ? _firstOutputTimeoutMs - elapsedMs
                    : _inactivityTimeoutMs - silenceMs;
            }

            if (firedCeiling || firedStall || firedWedge)
            {
                if (!ct.IsCancellationRequested)
                    _kill.Cancel();

                var outcome = firedCeiling ? Outcome.FiredAbsoluteCeiling
                    : firedStall ? Outcome.FiredStall
                    : Outcome.FiredSocketWedge;
                return new Result(outcome, lastSource, silenceMs);
            }

            // Emit a diagnostic heartbeat line every ~60 s so the watchdog's
            // internal view of pulse history is visible in run.log — future
            // incidents become diagnosable from artifacts alone.
            if (_onHeartbeat is not null)
            {
                var heartbeatElapsed = TicksToMs(nowTicks - _lastHeartbeatTimestamp);
                if (heartbeatElapsed >= HeartbeatIntervalMs)
                {
                    _lastHeartbeatTimestamp = nowTicks;
                    _onHeartbeat(
                        $"silenceMs={silenceMs} lastPulseSource={lastSource} deadlineMs={heartbeatDeadlineMs}");
                }
            }

            // Compute sleep duration: cap at 200 ms, but reduce to the nearest
            // deadline so we never overshoot.
            var deadlineMs = heartbeatDeadlineMs;

            if (_absoluteCeilingMs > 0)
            {
                var ceilingRemaining = _absoluteCeilingMs - TicksToMs(nowTicks - _startTimestamp);
                if (ceilingRemaining < deadlineMs)
                    deadlineMs = ceilingRemaining;
            }

            // Math.Max(1L, …) already floors the delay at 1 ms, so the value is
            // never <= 0 — no further clamp needed.
            var delay = Math.Min(200L, Math.Max(1L, deadlineMs));

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
