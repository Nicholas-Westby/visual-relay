using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Regression for the 2026-06-21 socket-wedge FALSE-KILL: a healthy, actively-working
/// swival agent on a frozen-trace filesystem was killed by the socket-wedge path
/// because the gate trusted a SINGLE idle CPU-sample window. The fix requires
/// SUSTAINED idleness (continuous idle for the whole inactivity window) so the
/// cpu-pulse liveness signal is honored on the wedge path too. A genuinely wedged
/// (recv-blocked, dead-socket) agent stays idle the entire window and is STILL killed.
/// </summary>
public sealed partial class ActivityWatchdogSocketWedgeTests
{
    // Wedge-sample pump emulating a BURSTY working agent: a CPU burst (busy sample)
    // then several idle samples, repeating — so sustained-idle never reaches a full
    // window. The token is a PARAMETER (not a captured `using` local) so the pump
    // owns no disposable it could outlive (mirrors StartCpuPulsePump).
    private static Task StartBurstyWedgePump(ActivityWatchdog watchdog, int windowMs, CancellationToken stop) =>
        Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(
                    SubtreeIdle: false, BackendSocketEstablished: true)); // CPU burst
                for (var i = 0; i < 6 && !stop.IsCancellationRequested; i++)
                {
                    watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(
                        SubtreeIdle: true, BackendSocketEstablished: true)); // idle between bursts
                    try { await Task.Delay(windowMs / 8, stop); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, stop);

    // Wedge-sample pump emulating a SUSTAINED-idle wedge: only idle samples (socket
    // up), refreshed so the verdict never goes stale. Token is a parameter, as above.
    private static Task StartIdleWedgePump(ActivityWatchdog watchdog, CancellationToken stop) =>
        Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                watchdog.RecordWedgeSample(new ActivityWatchdog.WedgeSample(
                    SubtreeIdle: true, BackendSocketEstablished: true));
                try { await Task.Delay(50, stop); }
                catch (OperationCanceledException) { return; }
            }
        }, stop);

    /// <summary>
    /// THE 2026-06-21 INCIDENT (must NOT fire): a healthy, actively-working agent on
    /// a frozen-trace filesystem. Real output is silent past the inactivity window and
    /// a backend socket is ESTABLISHED, but the agent has bursty CPU — between model
    /// turns the latest sample reads idle, yet it had a busy burst WITHIN the window.
    /// The single-sample gate false-killed this; the sustained-idle gate must not. A
    /// cpu pump masks the ordinary deadline the whole time — the watchdog must stay
    /// disarmed across several windows.
    /// </summary>
    [Fact]
    public async Task WaitAsync_BurstyAgent_IdleSampleButRecentCpuBurst_NotKilled()
    {
        const int inactivityMs = 1_000;
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill);

        watchdog.Pulse("trace"); // early real output, then trace freezes

        using var pumpCts = new CancellationTokenSource();
        var cpuPump = StartCpuPulsePump(watchdog, pumpCts.Token);
        var samplePump = StartBurstyWedgePump(watchdog, inactivityMs, pumpCts.Token);

        // Observe across several inactivity windows; a healthy agent must survive.
        using var stopCts = new CancellationTokenSource(inactivityMs * 5);
        var result = await watchdog.WaitAsync(stopCts.Token);

        await pumpCts.CancelAsync();
        await cpuPump;
        await samplePump;

        Assert.Equal(ActivityWatchdog.Outcome.Disarmed, result.Outcome);
        Assert.False(kill.IsCancellationRequested);
    }

    /// <summary>
    /// TRUE WEDGE through the loop (must STILL fire): a recv()-blocked dead-socket
    /// agent stays idle the ENTIRE window — every wedge sample reports idle — with a
    /// backend socket ESTABLISHED and real output silent. The cpu pump masks the
    /// ordinary deadline (exactly what hid the production wedge). The sustained-idle
    /// gate must still fire the socket-wedge kill. Preserves the 3ab3ce6 behavior.
    /// </summary>
    [Fact]
    public async Task WaitAsync_SustainedIdlePlusSocket_StillFiresSocketWedge()
    {
        const int inactivityMs = 1_200;
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000, inactivityTimeoutMs: inactivityMs,
            absoluteCeilingMs: 0, kill);

        watchdog.Pulse("trace");

        using var pumpCts = new CancellationTokenSource();
        var cpuPump = StartCpuPulsePump(watchdog, pumpCts.Token);
        var samplePump = StartIdleWedgePump(watchdog, pumpCts.Token);

        var sw = Stopwatch.StartNew();
        var result = await watchdog.WaitAsync(CancellationToken.None);
        sw.Stop();

        await pumpCts.CancelAsync();
        await cpuPump;
        await samplePump;

        Assert.Equal(ActivityWatchdog.Outcome.FiredSocketWedge, result.Outcome);
        Assert.True(kill.IsCancellationRequested);
        // Fires once sustained-idle crosses the window (≈ inactivityMs), not at start.
        Assert.True(sw.ElapsedMilliseconds >= inactivityMs - 300,
            $"fired before sustained-idle window elapsed: {sw.ElapsedMilliseconds}ms (window {inactivityMs}ms)");
        Assert.True(sw.ElapsedMilliseconds < inactivityMs + 3_000,
            $"socket-wedge fired too late: {sw.ElapsedMilliseconds}ms (window {inactivityMs}ms)");
    }
}
