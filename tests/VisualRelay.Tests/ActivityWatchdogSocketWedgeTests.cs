using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// End-to-end smoke tests for the socket-wedge detector through the real
/// production wiring (ProcessCapture cpu sampler → wedge sample →
/// ActivityWatchdog decision → killToken → tree kill). These tests spawn
/// real OS child processes and must be serialized in the Watchdog collection.
/// Virtualized WaitAsync and pure-decision TryDecideSocketWedge tests moved to
/// ActivityWatchdogDecisionTests.cs.
/// </summary>
[Collection("Watchdog")]
public sealed partial class ActivityWatchdogSocketWedgeTests
{
    /// <summary>
    /// End-to-end through the REAL production wiring (ProcessCapture cpu sampler →
    /// wedge sample → ActivityWatchdog decision → killToken → tree kill), with only
    /// the backend socket faked via the injected probe. The synthetic child writes
    /// an early real-output burst then goes idle at ~0 CPU (block-forever) — a
    /// true socket wedge. It must be killed near the inactivity window, never
    /// left to block forever.
    ///
    /// Note on the decisive outcome: when the agent subtree is genuinely idle, the
    /// CPU pulse does NOT fire, so the ordinary inactivity timer reaches the
    /// deadline first (<see cref="ActivityWatchdog.Outcome.FiredStall"/>). The
    /// socket-wedge path is the decisive one only when an EXTERNAL CPU source masks
    /// the deadline (a single real child cannot be both "cpu busy" and "subtree
    /// idle" — that decoupling is what made the production bug possible, and is
    /// proven at the watchdog layer by
    /// WaitAsync_CpuPulseMasksDeadline_SocketWedgeStillFires). Either
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

        // Child: burst of real output (stderr), then idle at ~0 CPU forever — the
        // watchdog's kill is the only thing that ends it (no self-exit ceiling).
        var script = "echo 'first token' 1>&2; exec tail -f /dev/null";

        var processTask = ProcessCapture.RunAsync(
            "/bin/sh", $"-c \"{script}\"", Directory.GetCurrentDirectory(),
            Timeout.InfiniteTimeSpan, CancellationToken.None,
            killToken: watchdogCts.Token,
            onActivity: watchdog.Pulse,
            cpuSampleIntervalMs: 1_000,
            onWedgeSample: watchdog.RecordWedgeSample,
            socketProbe: () => true); // backend socket is "ESTABLISHED"

        var watchdogTask = watchdog.WaitAsync(watchdogCts.Token);

        // Regression backstop: with a block-forever child and absoluteCeilingMs:0
        // there is no other ceiling, so a regressed watchdog would hang the
        // Task.WhenAny below forever. Cancel after the inactivity window + 8s so a
        // regression trips the kill, WaitAsync returns Disarmed, and the
        // kill-outcome assertion fails fast instead of hanging.
        watchdogCts.CancelAfter(inactivityMs + 8_000);

        var sw = Stopwatch.StartNew();
        var first = await Task.WhenAny(processTask, watchdogTask);
        var wd = await watchdogTask;
        var captured = await processTask;
        sw.Stop();

        // The wedged (idle) agent is killed via a fire path (stall or socket-wedge),
        // never left to block forever.
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
