using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Regression for the 2026-06-20 socket-wedge recurrence: a swival stage agent
/// held an ESTABLISHED TCP socket to the litellm backend, its process subtree sat
/// at ~0% CPU, and it produced ZERO stage output for ~22 minutes — yet the
/// watchdog never killed it because the cpu-pulse kept resetting the inactivity
/// deadline (the CPU it measured was accruing somewhere OTHER than the wedged
/// agent, e.g. a busy wrapper/sibling, so it masked the dead agent).
///
/// These tests exercise the additive socket-wedge detector with the CPU/socket/
/// clock sampling made injectable (hermetic — no real :4000). They prove a real
/// wedge is now caught AND that a healthy agent (recent output, OR a busy subtree,
/// OR no open backend socket) is NOT false-killed.
/// </summary>
public sealed partial class ActivityWatchdogSocketWedgeTests
{
    // A background "cpu" pulse pump: keeps resetting the ordinary inactivity
    // deadline (exactly what masked the wedge in production) until cancelled. The
    // token is a parameter (not a captured `using` local) so the pump owns no
    // disposable it could outlive.
    private static Task StartCpuPulsePump(ActivityWatchdog watchdog, CancellationToken stop) =>
        Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                watchdog.Pulse("cpu");
                try { await Task.Delay(50, stop); }
                catch (OperationCanceledException) { return; }
            }
        }, stop);

    // ── (A) Pure decision gate — the exact production algorithm ────────────────

    /// <summary>
    /// FOUR gates must hold to declare a wedge: real-output silence ≥ the inactivity
    /// window, the agent subtree SUSTAINED-idle for ≥ the inactivity window (a single
    /// idle sample window is NOT enough — a bursty/working agent shows transient idle
    /// windows between model turns), an ESTABLISHED backend socket, and first output
    /// seen. Anything less (recent output, recently-busy subtree, no socket, or before
    /// first output) must NOT fire — proving the detector is strictly additive and the
    /// cpu-pulse liveness signal is honored on this path too.
    /// </summary>
    [Theory]
    // firstPulse, silenceMs, inactivityMs, subtreeIdleForMs, subtreeIdle, socket, expectWedge
    [InlineData(true, 6_000, 6_000, 6_000, true, true, true)]    // exact wedge — fires
    [InlineData(true, 9_999, 6_000, 9_999, true, true, true)]    // well past window — fires
    [InlineData(true, 5_999, 6_000, 5_999, true, true, false)]   // silence just under window
    [InlineData(true, 60_000, 6_000, 60_000, false, true, false)] // subtree BUSY now — no kill
    [InlineData(true, 60_000, 6_000, 60_000, true, false, false)] // no backend socket — no kill
    [InlineData(false, 60_000, 6_000, 60_000, true, true, false)] // before first output — no kill
    // INCIDENT shape: real-output silent + latest sample idle + socket up, BUT a CPU
    // burst happened within the window (sustained-idle < window) — healthy, no kill.
    [InlineData(true, 60_000, 6_000, 2_000, true, true, false)]
    [InlineData(true, 60_000, 6_000, 5_999, true, true, false)]  // sustained-idle just under window
    public void TryDecideSocketWedge_FiresOnlyWhenAllGatesHold(
        bool firstPulse, long silenceMs, int inactivityMs, long subtreeIdleForMs,
        bool subtreeIdle, bool socket, bool expectWedge)
    {
        var decided = ActivityWatchdog.TryDecideSocketWedge(
            firstPulse, silenceMs, subtreeIdleForMs, inactivityMs,
            new ActivityWatchdog.WedgeSample(subtreeIdle, socket));

        Assert.Equal(expectWedge, decided);
    }

    // ── (B) Watchdog loop — cpu pulses mask the deadline, wedge still fires ─────

    /// <summary>
    /// The incident shape: a real-output burst, then the "cpu" pulse keeps firing
    /// (masking the inactivity deadline) while the wedge sample reports an idle
    /// subtree + an ESTABLISHED backend socket. The watchdog must still fire — via
    /// the socket-wedge path, not the ordinary inactivity path.
    /// </summary>
    [Fact]
    public async Task WaitAsync_CpuPulseMasksDeadline_SocketWedgeStillFires()
    {
        const int inactivityMs = 1_500;
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill);

        // Early real output, then idle + socket established.
        watchdog.Pulse("trace");
        watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: true, BackendSocketEstablished: true));

        // A pump that keeps the "cpu" pulse alive the whole time — exactly what
        // masked the deadline in production. If the ordinary inactivity path were
        // the only guard, this would keep the watchdog disarmed forever.
        using var pumpCts = new CancellationTokenSource();
        var pump = StartCpuPulsePump(watchdog, pumpCts.Token);

        var sw = Stopwatch.StartNew();
        var result = await watchdog.WaitAsync(CancellationToken.None);
        sw.Stop();
        await pumpCts.CancelAsync();
        await pump;

        Assert.Equal(ActivityWatchdog.Outcome.FiredSocketWedge, result.Outcome);
        Assert.True(kill.IsCancellationRequested);
        // Fires near the inactivity window despite the cpu pulse masking; generous
        // upper bound for the 200 ms poll loop + scheduling jitter.
        Assert.True(sw.ElapsedMilliseconds < inactivityMs + 3_000,
            $"socket-wedge fired too late: {sw.ElapsedMilliseconds}ms (window {inactivityMs}ms)");
        // The wedge autopsy must report real-output silence (≥ the window), NOT the
        // ~one-sample silence-since-any-pulse the cpu pump keeps at a few ms — the
        // 2026-06-21 incident's "4s kill" autopsy came from that misreport.
        Assert.True(result.SilenceMs >= inactivityMs,
            $"wedge SilenceMs should report real-output silence ≥ {inactivityMs}ms, got {result.SilenceMs}ms");
        Assert.True(result.SubtreeIdleForMs >= inactivityMs,
            $"wedge should report sustained-idle ≥ {inactivityMs}ms, got {result.SubtreeIdleForMs}ms");
    }

    /// <summary>
    /// Conservative guard: a genuinely-working agent whose target filesystem froze
    /// its trace view (the 2026-06-10 false-kill class) keeps a BUSY subtree even
    /// though real output is silent and a backend socket is open. The cpu pulse
    /// keeps the deadline alive AND the wedge sample reports a busy subtree, so the
    /// watchdog must NOT fire. We assert it stays disarmed for several windows.
    /// </summary>
    [Fact]
    public async Task WaitAsync_BusySubtree_NotKilled_EvenWithSocketAndSilence()
    {
        const int inactivityMs = 800;
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill);

        watchdog.Pulse("trace");
        // Busy subtree (NOT idle) + socket established + real output silent.
        watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: false, BackendSocketEstablished: true));

        using var pumpCts = new CancellationTokenSource();
        var pump = StartCpuPulsePump(watchdog, pumpCts.Token);

        // Watch for several inactivity windows; a healthy busy agent must survive.
        using var stopCts = new CancellationTokenSource(inactivityMs * 4);
        var result = await watchdog.WaitAsync(stopCts.Token);

        await pumpCts.CancelAsync();
        await pump;

        // Disarmed by our observation timeout, never fired.
        Assert.Equal(ActivityWatchdog.Outcome.Disarmed, result.Outcome);
        Assert.False(kill.IsCancellationRequested);
    }

    // ── (C) Full ProcessCapture wiring — synthetic wedge child, injected socket ─

    /// <summary>
    /// End-to-end through the REAL production wiring (ProcessCapture cpu sampler →
    /// wedge sample → ActivityWatchdog decision → killToken → tree kill), with only
    /// the backend socket faked via the injected probe. The synthetic child writes
    /// an early real-output burst then goes idle at ~0 CPU ("sleep") — a true
    /// socket wedge. It must be killed near the inactivity window (not after the
    /// child's own 60s sleep).
    ///
    /// Note on the decisive outcome: when the agent subtree is genuinely idle, the
    /// CPU pulse does NOT fire, so the ordinary inactivity timer reaches the
    /// deadline first (<see cref="ActivityWatchdog.Outcome.FiredStall"/>). The
    /// socket-wedge path is the decisive one only when an EXTERNAL CPU source masks
    /// the deadline (a single real child cannot be both "cpu busy" and "subtree
    /// idle" — that decoupling is what made the production bug possible, and is
    /// proven at the watchdog layer by
    /// <see cref="WaitAsync_CpuPulseMasksDeadline_SocketWedgeStillFires"/>). Either
    /// kill path means the wedged agent dies on schedule, which is what this
    /// end-to-end test asserts.
    /// </summary>
    [Fact]
    public async Task ProcessCapture_SyntheticWedge_IdleChildPlusSocket_IsKilled()
    {
        // cpu sampling needs ps(1); skip cleanly where it is unavailable.
        if (ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId) is null)
            return;

        const int inactivityMs = 5_000;
        using var watchdogCts = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 60_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, watchdogCts);

        // Child: burst of real output (stderr), then idle at ~0 CPU for 60s.
        var script = "echo 'first token' 1>&2; sleep 60";

        var processTask = ProcessCapture.RunAsync(
            "/bin/sh", $"-c \"{script}\"", Directory.GetCurrentDirectory(),
            Timeout.InfiniteTimeSpan, CancellationToken.None,
            killToken: watchdogCts.Token,
            onActivity: watchdog.Pulse,
            cpuSampleIntervalMs: 1_000,
            onWedgeSample: watchdog.RecordWedgeSample,
            socketProbe: () => true); // backend socket is "ESTABLISHED"

        var watchdogTask = watchdog.WaitAsync(watchdogCts.Token);

        var sw = Stopwatch.StartNew();
        var first = await Task.WhenAny(processTask, watchdogTask);
        var wd = await watchdogTask;
        var captured = await processTask;
        sw.Stop();

        // The wedged (idle) agent is killed via a fire path (stall or socket-wedge),
        // never left to run out its 60s sleep.
        Assert.True(
            wd.Outcome is ActivityWatchdog.Outcome.FiredStall or ActivityWatchdog.Outcome.FiredSocketWedge,
            $"expected a kill outcome, got {wd.Outcome}");
        Assert.True(watchdogCts.IsCancellationRequested);
        Assert.True(sw.ElapsedMilliseconds < inactivityMs + 8_000,
            $"expected kill near {inactivityMs}ms window, took {sw.ElapsedMilliseconds}ms");
        Assert.Contains("first token", captured.Output, StringComparison.Ordinal);
        Assert.Equal(watchdogTask, first);
    }

    /// <summary>
    /// Conservative counter-case through the same real wiring: a child that keeps
    /// its subtree BUSY (a CPU spin) past the inactivity window — with the socket
    /// probe still reporting ESTABLISHED and no further real output — must NOT be
    /// killed. This proves the detector cannot false-kill a working agent whose fs
    /// view is frozen; the busy subtree fails the idle gate.
    /// </summary>
    [Fact]
    public async Task ProcessCapture_BusyChildPlusSocket_NotKilled()
    {
        if (ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId) is null)
            return;

        const int inactivityMs = 4_000;
        using var watchdogCts = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 60_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, watchdogCts);

        // Child: one real-output line, then a tight CPU spin for ~10s (busy subtree,
        // no further output) — the fs-frozen-but-working shape.
        var script = "echo 'first token' 1>&2; end=$((SECONDS+10)); while [ $SECONDS -lt $end ]; do :; done";

        var processTask = ProcessCapture.RunAsync(
            "/bin/sh", $"-c \"{script}\"", Directory.GetCurrentDirectory(),
            Timeout.InfiniteTimeSpan, CancellationToken.None,
            killToken: watchdogCts.Token,
            onActivity: watchdog.Pulse,
            cpuSampleIntervalMs: 1_000,
            onWedgeSample: watchdog.RecordWedgeSample,
            socketProbe: () => true);

        var watchdogTask = watchdog.WaitAsync(watchdogCts.Token);

        // Stop observing shortly after the spin ends; the process exits on its own.
        var captured = await processTask;
        await watchdogCts.CancelAsync();
        var wd = await watchdogTask;

        // The child completed normally; the watchdog never fired a kill.
        Assert.Equal(0, captured.ExitCode);
        Assert.False(captured.TimedOut);
        Assert.NotEqual(ActivityWatchdog.Outcome.FiredSocketWedge, wd.Outcome);
        Assert.NotEqual(ActivityWatchdog.Outcome.FiredStall, wd.Outcome);
    }
}
