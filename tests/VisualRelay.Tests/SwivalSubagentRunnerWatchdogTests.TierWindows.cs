using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Tier-window decision + resolution tests (sleep-free), split out of
// SwivalSubagentRunnerWatchdogTests.cs to keep each file under the 300-line guard.
//
// These replace the former real-time integration tests, which ran a fake-swival
// bash child that paced pulses with `sleep` over real seconds purely to advance
// the wall clock. The watchdog's TIME-BASED decision is now exercised directly via
// its pure ActivityWatchdog.DecideOutcome seam (mirroring the established
// ActivityWatchdog.TryDecideSocketWedge pure-gate pattern) with simulated
// elapsed/silence values — synchronous, microseconds, no real clock — and the
// runner→tier→window WIRING is pinned by ResolveTierWindows below.
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    /// <summary>
    /// Regression (4): periodic stdout + trace pulses keep silence well under the
    /// inactivity window, so a stage survives FAR past what would have been the old
    /// flat cap (10 s). With pulses every ~2 s (silence ≤ 2 s) against a 5 s
    /// inactivity window and no absolute ceiling, the decision is Disarmed at every
    /// elapsed point — including 16 s in, well past the old cap.
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
    /// Regression (5): when an absolute ceiling is set it kills the stage despite
    /// continuous activity. Even with tiny silence (1 s pulses), once elapsed reaches
    /// the 10 s ceiling the decision is FiredAbsoluteCeiling — and just before it,
    /// Disarmed.
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
    /// Regression (6): per-tier inactivity windows are honored. The SAME 8 s of
    /// silence kills the cheap tier (3 s window → FiredStall) but is survived by the
    /// frontier tier (30 s window → Disarmed).
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

    /// <summary>
    /// Runner→watchdog WIRING: <see cref="SwivalSubagentRunner.ResolveTierWindows"/>
    /// maps each configured tier to ITS first-output/inactivity window and falls back
    /// to the flat defaults for an unmapped tier. This pins the coverage the former
    /// per-tier integration tests provided (that "frontier" resolves to its larger
    /// window, not "cheap"'s) without a real clock.
    /// </summary>
    [Fact]
    public void ResolveTierWindows_MapsEachConfiguredTier_ElseFlatFallback()
    {
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000, ["balanced"] = 120_000, ["frontier"] = 30_000
            },
            FirstOutputTimeoutMs = 99_000,
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 3_000, ["balanced"] = 600_000, ["frontier"] = 45_000
            },
            InactivityTimeoutMs = 88_000
        };

        Assert.Equal((2_000, 3_000), SwivalSubagentRunner.ResolveTierWindows(config, "cheap"));
        Assert.Equal((120_000, 600_000), SwivalSubagentRunner.ResolveTierWindows(config, "balanced"));
        Assert.Equal((30_000, 45_000), SwivalSubagentRunner.ResolveTierWindows(config, "frontier"));
        // Unmapped tier → the flat fallbacks.
        Assert.Equal((99_000, 88_000), SwivalSubagentRunner.ResolveTierWindows(config, "unknown"));
    }

    /// <summary>
    /// WIRING edge: a null per-tier inactivity map falls back to the flat
    /// <see cref="RelayConfig.InactivityTimeoutMs"/> for every tier (the default
    /// config shape), while the first-output map still resolves.
    /// </summary>
    [Fact]
    public void ResolveTierWindows_NullInactivityMap_FallsBackToFlatInactivity()
    {
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 2_000 },
            InactivityTimeoutMsByTier = null,
            InactivityTimeoutMs = 600_000
        };

        Assert.Equal((2_000, 600_000), SwivalSubagentRunner.ResolveTierWindows(config, "cheap"));
    }
}
