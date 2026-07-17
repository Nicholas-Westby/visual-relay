using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Decision-seam and tier-window resolution tests for <see cref="ActivityWatchdog"/>.
/// Pure decision functions (DecideOutcome, TryDecideSocketWedge, ResolveTierWindows)
/// driven with synthetic timestamps — no processes, no waiting, no clock sensitivity.
/// These tests run in parallel with all other non-Watchdog tests.
/// </summary>
public sealed partial class ActivityWatchdogDecisionTests
{
    // Thin wrapper over the pure production decision (ActivityWatchdog.DecideOutcome)
    // so each test sets only the fields its scenario exercises. The inert defaults
    // (huge windows, no ceiling, no wedge sample) mean the decision is Disarmed
    // unless a real threshold is crossed — every fire is therefore explicit.
    private static ActivityWatchdog.Outcome Decide(
        long elapsedMs = 0,
        long silenceMs = 0,
        long realOutputSilenceMs = 0,
        long subtreeIdleForMs = 0,
        bool firstPulseReceived = true,
        int firstOutputTimeoutMs = 3_600_000,
        int inactivityTimeoutMs = 3_600_000,
        int absoluteCeilingMs = 0,
        int outputSilenceTimeoutMs = 0,
        ActivityWatchdog.WedgeSample sample = default) =>
        ActivityWatchdog.DecideOutcome(
            elapsedMs, silenceMs, realOutputSilenceMs, subtreeIdleForMs,
            firstPulseReceived, firstOutputTimeoutMs, inactivityTimeoutMs,
            absoluteCeilingMs, outputSilenceTimeoutMs, sample);

    /// <summary>
    /// Per-tier first-output window (frontier vs cheap). 3 s slow start FiresStall at
    /// cheap's 2 s window but is Disarmed at frontier's 30 s window.
    /// </summary>
    [Fact]
    public void DecideOutcome_BeforeFirstOutput_FrontierWindowSurvivesSlowStartThatKillsCheap()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(elapsedMs: 3_000, firstPulseReceived: false, firstOutputTimeoutMs: 2_000));

        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 3_000, firstPulseReceived: false, firstOutputTimeoutMs: 30_000));
    }

    [Fact]
    public void DecideOutcome_AfterFirstOutput_LongSilenceUnderInactivityWindow_Disarmed()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 15_000, silenceMs: 15_000, firstPulseReceived: true,
                firstOutputTimeoutMs: 10_000, inactivityTimeoutMs: 600_000));
    }

    /// <summary>
    /// Regression (1): stdout bytes are liveness pulses, so a process writing stdout
    /// but NO trace file is NOT killed by the first-output watchdog. A stdout pulse
    /// sets firstPulseReceived, flipping into the inactivity phase.
    /// </summary>
    [Fact]
    public void DecideOutcome_StdoutPulseDisarmsFirstOutput_SurvivesPastFirstOutputWindow()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 2_500, silenceMs: 500, firstPulseReceived: true,
                firstOutputTimeoutMs: 2_000, inactivityTimeoutMs: 600_000));
    }

    /// <summary>
    /// Regression (3): A process that pulses once, then goes silent
    /// past the inactivity window. Decision-seam test.
    /// </summary>
    [Fact]
    public void DecideOutcome_FirstPulseThenSilence_FiresAtInactivityDeadline()
    {
        // One pulse at 0 ms → firstPulseReceived=true.  Then 4 s of total
        // silence with a 3 s inactivity window → FiredStall.
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(elapsedMs: 4_000, silenceMs: 4_000, firstPulseReceived: true,
                firstOutputTimeoutMs: 90_000, inactivityTimeoutMs: 3_000));
    }

    /// <summary>
    /// Absolute ceiling fires even when the agent is pulsing frequently
    /// (low silenceMs keeps the inactivity watchdog disarmed).  The ceiling
    /// is a hard wall-clock backstop, not a progress heuristic.
    /// </summary>
    [Fact]
    public void DecideOutcome_FiresAbsoluteCeiling_WhenElapsedExceedsCeiling()
    {
        // Elapsed meets the 5 s ceiling; silence is only 500 ms (active pulses).
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredAbsoluteCeiling,
            Decide(elapsedMs: 5_000, silenceMs: 500, firstPulseReceived: true,
                inactivityTimeoutMs: 600_000, absoluteCeilingMs: 5_000));
    }

    /// <summary>
    /// When both ceiling AND stall are met, the ceiling wins — FiredAbsoluteCeiling.
    /// </summary>
    [Fact]
    public void DecideOutcome_CeilingWinsOverStall()
    {
        // Both ceiling (elapsed ≥ 5s) and stall (silence ≥ 3s inactivity window)
        // are met.  Ceiling must win.
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredAbsoluteCeiling,
            Decide(elapsedMs: 5_000, silenceMs: 5_000, firstPulseReceived: true,
                inactivityTimeoutMs: 3_000, absoluteCeilingMs: 5_000));
    }

    /// <summary>
    /// Under the ceiling, only the stall path can fire — ceiling is not reached.
    /// </summary>
    [Fact]
    public void DecideOutcome_DoesNotFireCeiling_WhenUnderCeiling()
    {
        // Elapsed 4 s is under the 10 s ceiling; silence 5 s exceeds the 3 s
        // inactivity window → FiredStall, NOT the ceiling.
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(elapsedMs: 4_000, silenceMs: 5_000, firstPulseReceived: true,
                inactivityTimeoutMs: 3_000, absoluteCeilingMs: 10_000));
    }

    /// <summary>
    /// When absoluteCeilingMs is 0 (disabled), the ceiling path is inert even
    /// at extreme elapsed durations — only the stall path can fire.  This
    /// documents the pre-fix behavior: a flailing-but-active agent would run
    /// unbounded.
    /// </summary>
    [Fact]
    public void DecideOutcome_CeilingDisabled_DoesNotFire()
    {
        // Ceiling is 0 (disabled).  Elapsed 100 s is huge, but the ceiling
        // short-circuit (absoluteCeilingMs > 0) is false, so only stall fires.
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(elapsedMs: 100_000, silenceMs: 10_000, firstPulseReceived: true,
                inactivityTimeoutMs: 5_000, absoluteCeilingMs: 0));
    }

    /// <summary>
    /// Regression (4): periodic pulses keep silence under the inactivity window,
    /// surviving FAR past the old flat cap (10 s). With pulses every ~2 s and a
    /// 5 s inactivity window, stays Disarmed at every elapsed point up to 16 s.
    /// </summary>
    [Fact]
    public void DecideOutcome_PulsesResetSilence_DisarmedFarPastOldFlatCap()
    {
        for (var elapsedMs = 2_000L; elapsedMs <= 16_000L; elapsedMs += 2_000L)
        {
            Assert.Equal(
                ActivityWatchdog.Outcome.Disarmed,
                Decide(elapsedMs: elapsedMs, silenceMs: 2_000, firstPulseReceived: true,
                    inactivityTimeoutMs: 5_000, absoluteCeilingMs: 0));
        }
    }

    /// <summary>
    /// Regression (5): absolute ceiling kills despite continuous pulses.
    /// </summary>
    [Fact]
    public void DecideOutcome_AbsoluteCeilingReached_FiresDespiteContinuousActivity()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 9_000, silenceMs: 500, firstPulseReceived: true,
                inactivityTimeoutMs: 600_000, absoluteCeilingMs: 10_000));

        Assert.Equal(
            ActivityWatchdog.Outcome.FiredAbsoluteCeiling,
            Decide(elapsedMs: 10_000, silenceMs: 500, firstPulseReceived: true,
                inactivityTimeoutMs: 600_000, absoluteCeilingMs: 10_000));
    }

    /// <summary>
    /// Regression (6): same silence kills cheap (3 s window) but is survived by
    /// frontier (30 s window).
    /// </summary>
    [Fact]
    public void DecideOutcome_PerTierInactivityWindows_CheapKilledFrontierSurvivesSameSilence()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(silenceMs: 8_000, firstPulseReceived: true, inactivityTimeoutMs: 3_000));

        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(silenceMs: 8_000, firstPulseReceived: true, inactivityTimeoutMs: 30_000));
    }

    // ── (A) Pure decision gate — the exact production algorithm ────────────────

    /// <summary>
    /// FOUR gates: real-output silence ≥ window, sustained-idle ≥ window,
    /// established socket, and first output seen. Anything less must NOT fire.
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
}
