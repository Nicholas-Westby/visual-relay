using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class ActivityWatchdogDecisionTests
{
    // ── (C) Output-silence ceiling (hung-LLM guard) ─────────────────────────

    /// <summary>
    /// Hung-LLM shape: cpu pulses keep silenceMs low (the ordinary inactivity
    /// deadline is masked), but real output has been silent past the output-silence
    /// limit. The new gate fires FiredOutputSilence — NOT the inactivity stall
    /// (masked) or the socket wedge (subtree not necessarily idle).
    /// </summary>
    [Fact]
    public void DecideOutcome_FiresOutputSilence_WhenCpuPulsesMaskInactivityButRealOutputSilent()
    {
        // cpu pulse resets silenceMs to 500ms, but real-output silence is 10min.
        // Output-silence limit is 5min → fires.
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredOutputSilence,
            Decide(elapsedMs: 600_000, silenceMs: 500, realOutputSilenceMs: 600_000,
                firstPulseReceived: true,
                firstOutputTimeoutMs: 90_000, inactivityTimeoutMs: 1_200_000,
                absoluteCeilingMs: 0, outputSilenceTimeoutMs: 300_000));
    }

    [Fact]
    public void DecideOutcome_OutputSilence_BelowLimit_Disarmed()
    {
        // Real-output silent for 4min, limit is 5min → not yet fired.
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 300_000, silenceMs: 500, realOutputSilenceMs: 240_000,
                firstPulseReceived: true,
                inactivityTimeoutMs: 1_200_000,
                outputSilenceTimeoutMs: 300_000));
    }

    [Fact]
    public void DecideOutcome_OutputSilence_Disabled_DoesNotFire()
    {
        // outputSilenceTimeoutMs=0 (disabled). Huge real-output silence but no fire.
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 3_600_000, silenceMs: 500, realOutputSilenceMs: 3_600_000,
                firstPulseReceived: true,
                inactivityTimeoutMs: 1_200_000,
                outputSilenceTimeoutMs: 0));
    }

    [Fact]
    public void DecideOutcome_CeilingWinsOverOutputSilence()
    {
        // Both absolute ceiling (elapsed ≥ 5s) and output-silence
        // (real-output silent ≥ 3s) are met. Ceiling must win (priority 1).
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredAbsoluteCeiling,
            Decide(elapsedMs: 5_000, silenceMs: 500, realOutputSilenceMs: 5_000,
                firstPulseReceived: true,
                inactivityTimeoutMs: 600_000, absoluteCeilingMs: 5_000,
                outputSilenceTimeoutMs: 3_000));
    }

    [Fact]
    public void DecideOutcome_OutputSilenceWinsOverStall()
    {
        // Both inactivity stall (silenceMs ≥ 3s) and output-silence
        // (realOutputSilenceMs ≥ 3s) are met. Output-silence wins (priority 2 > 3).
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredOutputSilence,
            Decide(elapsedMs: 5_000, silenceMs: 5_000, realOutputSilenceMs: 5_000,
                firstPulseReceived: true,
                inactivityTimeoutMs: 3_000, outputSilenceTimeoutMs: 3_000));
    }

    [Fact]
    public void DecideOutcome_OutputSilenceWinsOverSocketWedge()
    {
        var wedgeSample = new ActivityWatchdog.WedgeSample(
            SubtreeIdle: true, BackendSocketEstablished: true);
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredOutputSilence,
            Decide(elapsedMs: 10_000, silenceMs: 500, realOutputSilenceMs: 10_000,
                subtreeIdleForMs: 10_000, firstPulseReceived: true,
                inactivityTimeoutMs: 3_000, outputSilenceTimeoutMs: 3_000,
                sample: wedgeSample));
    }

    [Fact]
    public void DecideOutcome_OutputSilence_BeforeFirstOutput_DoesNotFire()
    {
        // outputSilenceTimeoutMs is armed but firstPulseReceived=false → disarms.
        // First-output stall (elapsed=600s ≥ 90s) still fires.
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(elapsedMs: 600_000, silenceMs: 600_000, realOutputSilenceMs: 600_000,
                firstPulseReceived: false, firstOutputTimeoutMs: 90_000,
                outputSilenceTimeoutMs: 300_000));
    }

    [Fact]
    public void DecideOutcome_ContinuousRealOutput_OutputSilenceGateDisarmed()
    {
        // Real output pulsing every second keeps realOutputSilenceMs at 1s,
        // well under the 5min limit.
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 3_600_000, silenceMs: 1_000, realOutputSilenceMs: 1_000,
                firstPulseReceived: true,
                inactivityTimeoutMs: 600_000, outputSilenceTimeoutMs: 300_000));
    }
}
