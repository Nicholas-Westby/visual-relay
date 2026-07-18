using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Virtualized <see cref="ActivityWatchdog.WaitAsync"/> loop tests. A
/// <see cref="ManualTimeProvider"/> drives the watchdog polling loop; each scenario's
/// pulses and wedge samples are driven INLINE (once per sub-window step, in lockstep
/// with the clock advance) rather than by a background task — fully deterministic, no
/// real waiting, no <c>Task.Run</c> race. These tests do NOT need serialization and
/// run in parallel with all other non-Watchdog tests.
/// </summary>
public sealed partial class ActivityWatchdogDecisionTests
{
    // ── (B) Watchdog loop — cpu pulses mask the deadline, wedge still fires ─────

    /// <summary>
    /// The incident shape: a real-output burst, then the "cpu" pulse keeps firing
    /// (masking the inactivity deadline) while the wedge sample reports an idle
    /// subtree + an ESTABLISHED backend socket. The watchdog must still fire — via
    /// the socket-wedge path, not the ordinary inactivity path.
    /// Virtualized: a ManualTimeProvider drives the watchdog loop and pump delays
    /// instantly — no real waiting, no wall-clock sensitivity.
    /// </summary>
    [Fact]
    public async Task WaitAsync_CpuPulseMasksDeadline_SocketWedgeStillFires()
    {
        const int inactivityMs = 1_500;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill, timeProvider: time);

        // Early real output, then idle + socket established.
        watchdog.Pulse("trace");
        watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: true, BackendSocketEstablished: true));

        var watchdogTask = watchdog.WaitAsync(CancellationToken.None);
        // Inline: a "cpu" pulse each step keeps the ORDINARY inactivity deadline masked
        // (cpu never resets the real-output clock), while the wedge sample stays idle +
        // socket-established. Real-output silence and sustained-idleness both grow with
        // the clock, so the additive socket-wedge gate fires while the ordinary path
        // stays masked — the exact production incident, deterministic and pump-free.
        const int step = inactivityMs / 4;
        for (var i = 0; i < 60 && !watchdogTask.IsCompleted; i++)
        {
            watchdog.Pulse("cpu");
            watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: true, BackendSocketEstablished: true));
            time.Advance(TimeSpan.FromMilliseconds(step));
        }
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();
        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.FiredSocketWedge, result.Outcome);
        Assert.True(kill.IsCancellationRequested);
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
    /// Virtualized: ManualTimeProvider advances well past several inactivity windows
    /// instantly — the cpu pump and watchdog both see the same virtual clock.
    /// </summary>
    [Fact]
    public async Task WaitAsync_BusySubtree_NotKilled_EvenWithSocketAndSilence()
    {
        const int inactivityMs = 800;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill, timeProvider: time);

        watchdog.Pulse("trace");

        using var stopCts = new CancellationTokenSource();
        var watchdogTask = watchdog.WaitAsync(stopCts.Token);
        // Inline cpu pump: a pulse + BUSY wedge sample once per sub-window step, so the
        // inactivity deadline and the sustained-idle clock are BOTH continuously reset —
        // exactly what a real cpu pump does, but with deterministic ordering against the
        // virtual clock (no background task racing time advancement). Because a pulse
        // lands within every `step` (< window), the watchdog can never observe a full
        // window of silence or sustained-idleness whenever its loop reads the clock.
        // Twelve steps carry it across three full inactivity windows.
        const int step = inactivityMs / 4;
        for (var i = 0; i < 12 && !watchdogTask.IsCompleted; i++)
        {
            watchdog.Pulse("cpu");
            watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: false, BackendSocketEstablished: true));
            time.Advance(TimeSpan.FromMilliseconds(step));
        }
        await stopCts.CancelAsync();
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();
        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.Disarmed, result.Outcome);
        Assert.False(kill.IsCancellationRequested);
    }

    /// <summary>
    /// THE 2026-06-21 INCIDENT (must NOT fire): a healthy, actively-working agent on
    /// a frozen-trace filesystem. Real output is silent past the inactivity window and
    /// a backend socket is ESTABLISHED, but the agent has bursty CPU — between model
    /// turns the latest sample reads idle, yet it had a busy burst WITHIN the window.
    /// The single-sample gate false-killed this; the sustained-idle gate must not. A
    /// cpu pump masks the ordinary deadline the whole time — the watchdog must stay
    /// disarmed across several windows.
    /// Virtualized: ManualTimeProvider drives the watchdog loop and both pumps
    /// instantly — no real waiting.
    /// </summary>
    [Fact]
    public async Task WaitAsync_BurstyAgent_IdleSampleButRecentCpuBurst_NotKilled()
    {
        const int inactivityMs = 1_000;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill, timeProvider: time);

        watchdog.Pulse("trace"); // early real output, then trace freezes

        using var stopCts = new CancellationTokenSource();
        var watchdogTask = watchdog.WaitAsync(stopCts.Token);
        // Bursty inline pump: a cpu pulse every step keeps the ordinary deadline alive;
        // the wedge sample is mostly IDLE but bursts BUSY every third step, so the
        // sustained-idle clock never reaches a full window (a working agent between model
        // turns). Deterministic against the virtual clock — the busy burst lands within
        // every window, so the wedge gate's sustained-idle requirement is never met, no
        // matter when the loop reads the clock. Sixteen steps cross four windows.
        const int step = inactivityMs / 4;
        for (var i = 0; i < 16 && !watchdogTask.IsCompleted; i++)
        {
            watchdog.Pulse("cpu");
            watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(
                SubtreeIdle: i % 3 != 0, BackendSocketEstablished: true));
            time.Advance(TimeSpan.FromMilliseconds(step));
        }
        await stopCts.CancelAsync();
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();
        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.Disarmed, result.Outcome);
        Assert.False(kill.IsCancellationRequested);
    }

    /// <summary>
    /// TRUE WEDGE through the loop (must STILL fire): a recv()-blocked dead-socket
    /// agent stays idle the ENTIRE window — every wedge sample reports idle — with a
    /// backend socket ESTABLISHED and real output silent. The cpu pump masks the
    /// ordinary deadline (exactly what hid the production wedge). The sustained-idle
    /// gate must still fire the socket-wedge kill. Preserves the 3ab3ce6 behavior.
    /// Virtualized: ManualTimeProvider drives the loop and both pumps instantly.
    /// </summary>
    [Fact]
    public async Task WaitAsync_SustainedIdlePlusSocket_StillFiresSocketWedge()
    {
        const int inactivityMs = 1_200;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill, timeProvider: time);

        watchdog.Pulse("trace");

        var watchdogTask = watchdog.WaitAsync(CancellationToken.None);
        // Inline: cpu pulses mask the ordinary deadline while every wedge sample is idle
        // + socket-established (a recv()-blocked dead-socket agent). Sustained idleness
        // and real-output silence both reach the window, so the socket-wedge gate fires.
        const int step = inactivityMs / 4;
        for (var i = 0; i < 60 && !watchdogTask.IsCompleted; i++)
        {
            watchdog.Pulse("cpu");
            watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(SubtreeIdle: true, BackendSocketEstablished: true));
            time.Advance(TimeSpan.FromMilliseconds(step));
        }
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();
        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.FiredSocketWedge, result.Outcome);
        Assert.True(kill.IsCancellationRequested);
        Assert.True(result.SilenceMs >= inactivityMs,
            $"wedge SilenceMs should be ≥ {inactivityMs}ms, got {result.SilenceMs}ms");
    }

    /// <summary>
    /// Unit test: drive <see cref="ActivityWatchdog"/> with a simulated
    /// pulse history matching the 2026-06-12 socket-wedge incident —
    /// pulses for the first ~2 min (simulated), then total silence for
    /// the full inactivity window.  The watchdog must fire at the
    /// inactivity deadline ± one polling interval (~200 ms).
    ///
    /// This test isolates the watchdog's deadline logic from all upstream
    /// pulse sources.  If it passes while the integration tests fail, the
    /// bug is upstream of the watchdog (in a <c>Pulse(...)</c> call site).
    /// Virtualized: ManualTimeProvider drives the loop and the 50ms inter-pulse
    /// gap instantly — no real waiting, no wall-clock sensitivity.
    /// </summary>
    [Fact]
    public async Task WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline()
    {
        const int inactivityTimeoutMs = 2_000;
        var time = new ManualTimeProvider();
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000,
            inactivityTimeoutMs: inactivityTimeoutMs,
            absoluteCeilingMs: 0,
            kill, timeProvider: time);

        // Simulate early output burst (representing the first ~2 min of real
        // activity: stdout lines, trace-file creation).
        watchdog.Pulse("stdout");
        // Model a real ~50ms gap between pulses via virtual time advance.
        time.Advance(TimeSpan.FromMilliseconds(50));
        watchdog.Pulse("trace");

        // Now total silence — no more pulses of any kind.
        var watchdogTask = watchdog.WaitAsync(CancellationToken.None);

        // Advance virtual time well past the inactivity deadline in a single
        // jump so the watchdog's internal poll loop sees the expired deadline.
        // ManualTimeProvider fires timers synchronously during Advance, so the
        // delay completes immediately.  We then give the async state machine a
        // few real-time ticks to run the deadline-check continuation.
        time.Advance(TimeSpan.FromMilliseconds(inactivityTimeoutMs + 200));
        for (var i = 0; i < 5 && !watchdogTask.IsCompleted; i++)
            await Task.Yield();

        var result = await watchdogTask;

        Assert.Equal(ActivityWatchdog.Outcome.FiredStall, result.Outcome);
        // Last real pulse before silence was "trace".
        Assert.Equal("trace", result.LastPulseSource);
        Assert.True(result.SilenceMs >= inactivityTimeoutMs,
            $"Expected silence >= {inactivityTimeoutMs}ms, got {result.SilenceMs}ms");
        Assert.True(kill.IsCancellationRequested);
    }
}
