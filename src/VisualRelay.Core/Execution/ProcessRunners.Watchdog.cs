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

    public readonly record struct Result(
        Outcome Outcome, string LastPulseSource, long SilenceMs, long SubtreeIdleForMs = 0);

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
    private readonly TimeProvider _timeProvider;
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

    // Time the agent subtree was last observed BUSY (a wedge sample with
    // SubtreeIdle == false). The socket-wedge gate requires SUSTAINED idleness —
    // continuous idle for the whole inactivity window — so a single transient idle
    // sample (a working agent between/within model turns) does NOT count as wedged.
    // This is what honors the cpu-pulse liveness signal on the wedge path: any CPU
    // burst within the window pushes this forward and disarms the wedge. Seeded at
    // start so the gate cannot fire before a full window of idleness has elapsed.
    private long _lastSubtreeBusyTimestamp;

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
        Action<string>? onHeartbeat = null,
        TimeProvider? timeProvider = null)
    {
        _firstOutputTimeoutMs = firstOutputTimeoutMs;
        _inactivityTimeoutMs = inactivityTimeoutMs;
        _absoluteCeilingMs = absoluteCeilingMs;
        _kill = kill;
        _onHeartbeat = onHeartbeat;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTimestamp = _timeProvider.GetTimestamp();
        _lastPulseTimestamp = _startTimestamp;
        _lastRealOutputTimestamp = _startTimestamp;
        _lastSubtreeBusyTimestamp = _startTimestamp;
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
        var now = _timeProvider.GetTimestamp();
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
            // Any busy sample resets the sustained-idle clock the wedge gate reads,
            // so a bursty/working agent (CPU active at any point in the window) is
            // never socket-wedge-killed even while its trace view is frozen.
            if (!sample.SubtreeIdle)
                _lastSubtreeBusyTimestamp = _timeProvider.GetTimestamp();
        }
    }

    /// <summary>
    /// Pure socket-wedge decision, exposed for hermetic unit testing of the exact
    /// production gate. Fires ONLY when all FOUR hold: (1) the agent has produced no
    /// real output (stdout/stderr/trace) for at least the full inactivity window —
    /// cpu pulses do not reset this clock; (2) the agent process subtree has been
    /// SUSTAINED-idle for at least the full inactivity window — a single recent idle
    /// sample is NOT enough, so any CPU burst within the window (a working agent
    /// between/within model turns) disarms the gate and the cpu-pulse liveness signal
    /// is honored here too; (3) the latest sample still reads idle; (4) a backend
    /// socket is still ESTABLISHED. Strictly additive: it can only fire inside a
    /// sustained silence the inactivity timeout already covers, and a healthy agent
    /// (recent output, OR a recent CPU burst, OR no open backend socket) fails a gate.
    /// </summary>
    internal static bool TryDecideSocketWedge(
        bool firstPulseReceived,
        long realOutputSilenceMs,
        long subtreeIdleForMs,
        int inactivityTimeoutMs,
        WedgeSample sample) =>
        firstPulseReceived
        && realOutputSilenceMs >= inactivityTimeoutMs
        && subtreeIdleForMs >= inactivityTimeoutMs
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
            long realOutputSilenceMs;
            long subtreeIdleForMs;
            long nowTicks;
            long heartbeatDeadlineMs;

            lock (_lock)
            {
                nowTicks = _timeProvider.GetTimestamp();
                var elapsedMs = TicksToMs(nowTicks - _startTimestamp);
                silenceMs = TicksToMs(nowTicks - _lastPulseTimestamp);
                realOutputSilenceMs = TicksToMs(nowTicks - _lastRealOutputTimestamp);
                subtreeIdleForMs = TicksToMs(nowTicks - _lastSubtreeBusyTimestamp);
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
                // inactivity window, agent subtree SUSTAINED-idle for the whole
                // window, backend socket ESTABLISHED).
                firedWedge = TryDecideSocketWedge(
                    _firstPulseReceived, realOutputSilenceMs, subtreeIdleForMs,
                    _inactivityTimeoutMs, _lastWedgeSample);

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
                // For the wedge outcome, report real-output silence (≈ the inactivity
                // window) — NOT silenceMs, which the cpu pulse keeps at ~one sample and
                // which made the autopsy read like a "4s kill" — plus sustained-idle.
                return outcome == Outcome.FiredSocketWedge
                    ? new Result(outcome, lastSource, realOutputSilenceMs, subtreeIdleForMs)
                    : new Result(outcome, lastSource, silenceMs);
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

            var delayTask = Task.Delay(TimeSpan.FromMilliseconds(delay), _timeProvider, ct);
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

    private long TicksToMs(long ticks) => (long)(ticks / (double)_timeProvider.TimestampFrequency * 1000.0);
}
