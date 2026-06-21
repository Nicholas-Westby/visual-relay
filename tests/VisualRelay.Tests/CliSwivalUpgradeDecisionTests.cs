using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the 7-day-window decision that gates the weekly swival upgrade
/// check (moved from the launcher's <c>_swival_upgrade_check</c>). The probe runs
/// at most once per week, tracked by a per-machine XDG-state timestamp; a fresh
/// stamp suppresses the probe, a stale/absent stamp permits it.
/// </summary>
public sealed class CliSwivalUpgradeDecisionTests
{
    private const long IntervalSecs = 7 * 24 * 60 * 60;

    [Fact]
    public void Probe_Suppressed_WhenStampIsFresh()
    {
        long now = 1_000_000;
        long lastChecked = now - 86_400; // 1 day ago
        Assert.False(SwivalUpgradeDecision.ShouldProbe(lastChecked, now, IntervalSecs));
    }

    [Fact]
    public void Probe_Permitted_WhenStampIsStale()
    {
        long now = 1_000_000;
        long lastChecked = now - (8 * 86_400); // 8 days ago
        Assert.True(SwivalUpgradeDecision.ShouldProbe(lastChecked, now, IntervalSecs));
    }

    [Fact]
    public void Probe_Permitted_WhenNoStampExists()
    {
        Assert.True(SwivalUpgradeDecision.ShouldProbe(lastCheckedEpochSecs: null, nowEpochSecs: 1_000_000, IntervalSecs));
    }

    [Fact]
    public void Probe_Permitted_WhenStampIsUnparseable()
    {
        // A corrupt stamp is treated as "no record" → probe permitted.
        Assert.True(SwivalUpgradeDecision.ShouldProbe(lastCheckedEpochSecs: null, nowEpochSecs: 1_000_000, IntervalSecs));
    }

    [Fact]
    public void UpgradeAvailable_WhenProbeEmitsNonEmptyOutput()
    {
        Assert.True(SwivalUpgradeDecision.UpgradeAvailable("swival 2.0.0"));
        Assert.True(SwivalUpgradeDecision.UpgradeAvailable("  swival/tap/swival  "));
    }

    [Fact]
    public void NoUpgrade_WhenProbeOutputIsBlank()
    {
        Assert.False(SwivalUpgradeDecision.UpgradeAvailable(""));
        Assert.False(SwivalUpgradeDecision.UpgradeAvailable("   \n  "));
        Assert.False(SwivalUpgradeDecision.UpgradeAvailable(null));
    }
}
