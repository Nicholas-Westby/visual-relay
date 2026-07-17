using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class ActivityWatchdogDecisionTests
{
    /// <summary>
    /// Runner→watchdog WIRING: <see cref="SwivalSubagentRunner.ResolveTierWindows"/>
    /// maps each configured tier to ITS first-output/inactivity window and falls back
    /// to the flat defaults for an unmapped tier.
    /// </summary>
    [Fact]
    public void ResolveTierWindows_MapsEachConfiguredTier_ElseFlatFallback()
    {
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 30_000
            },
            FirstOutputTimeoutMs = 99_000,
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 3_000,
                ["balanced"] = 600_000,
                ["frontier"] = 45_000
            },
            InactivityTimeoutMs = 88_000
        };

        Assert.Equal((2_000, 3_000, 0), SwivalSubagentRunner.ResolveTierWindows(config, "cheap"));
        Assert.Equal((120_000, 600_000, 0), SwivalSubagentRunner.ResolveTierWindows(config, "balanced"));
        Assert.Equal((30_000, 45_000, 0), SwivalSubagentRunner.ResolveTierWindows(config, "frontier"));
        Assert.Equal((99_000, 88_000, 0), SwivalSubagentRunner.ResolveTierWindows(config, "unknown"));
    }

    [Fact]
    public void ResolveTierWindows_NullInactivityMap_FallsBackToFlatInactivity()
    {
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 2_000 },
            InactivityTimeoutMsByTier = null,
            InactivityTimeoutMs = 600_000
        };
        Assert.Equal((2_000, 600_000, 0), SwivalSubagentRunner.ResolveTierWindows(config, "cheap"));
    }

    /// <summary>
    /// WIRING: per-tier output-silence override honored. Tier-specific value wins;
    /// otherwise the flat OutputSilenceTimeoutMs fallback applies.
    /// </summary>
    [Fact]
    public void ResolveTierWindows_OutputSilence_PerTierOverrideHonored()
    {
        var config = TestConfig() with
        {
            OutputSilenceTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 300_000,
                ["frontier"] = 900_000
            },
            OutputSilenceTimeoutMs = 600_000
        };
        Assert.Equal((90_000, 600_000, 300_000), SwivalSubagentRunner.ResolveTierWindows(config, "cheap"));
        Assert.Equal((660_000, 600_000, 900_000), SwivalSubagentRunner.ResolveTierWindows(config, "frontier"));
        Assert.Equal((120_000, 600_000, 600_000), SwivalSubagentRunner.ResolveTierWindows(config, "balanced"));
        var nullTierConfig = config with { OutputSilenceTimeoutMsByTier = null };
        Assert.Equal((90_000, 600_000, 600_000), SwivalSubagentRunner.ResolveTierWindows(nullTierConfig, "cheap"));
    }

    private static RelayConfig TestConfig() =>
        new(
            "llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true, 5_000, 300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000, 2,
            InactivityTimeoutMsByTier: null, InactivityTimeoutMs: 600_000);
}
