using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorAliasConsistencyTests
{
    // ── Cross-file tier-name consistency ────────────────────────────────

    /// <summary>
    /// (1) Every tier alias in <see cref="BackendConfigGenerator.Chains"/> has a
    ///     <see cref="BackendConfigGenerator.DefaultTierResolution"/> entry.
    /// (2) Every <see cref="BackendConfigGenerator.DefaultTierResolution"/> value has a
    ///     <see cref="RelayPricing.Default"/> entry.
    /// (3) No tier alias appears as a pricing key (concrete models only).
    /// (4) The balanced/cheap tier-alias names must match the swival profile.
    /// </summary>
    [Fact]
    public void TierAliasNames_AreConsistentAcrossBackendConfigPricingAndSwivalProfile()
    {
        var tierAliases = BackendConfigGenerator.Chains.Keys.ToHashSet(StringComparer.Ordinal);

        // 1. Every tier alias must have a DefaultTierResolution entry.
        foreach (var tier in tierAliases)
        {
            Assert.True(
                BackendConfigGenerator.DefaultTierResolution.ContainsKey(tier),
                $"tier '{tier}' missing from DefaultTierResolution");
        }

        // 2. Every DefaultTierResolution value must have a RelayPricing.Default entry.
        foreach (var (tier, concrete) in BackendConfigGenerator.DefaultTierResolution)
        {
            Assert.True(
                RelayPricing.Default.ContainsKey(concrete),
                $"DefaultTierResolution['{tier}'] = '{concrete}' missing from RelayPricing.Default");
        }

        // 3. Pricing keys must contain no tier alias.
        var pricingKeys = RelayPricing.Default.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var tier in tierAliases)
        {
            Assert.DoesNotContain(tier, pricingKeys);
        }

        // 4. Swival profile tier-name assertions (keep unchanged).
        var swivalModelValues = BackendConfigGeneratorTestHelpers.ParseSwivalProfileModelValues(
            SwivalProfileSession.DefaultToml);

        var balancedTier = swivalModelValues["balanced"];
        var cheapTier = swivalModelValues["cheap"];

        Assert.DoesNotContain("-kimi", balancedTier, StringComparison.Ordinal);
        Assert.DoesNotContain("-kimi", cheapTier, StringComparison.Ordinal);
        Assert.Equal("balanced", balancedTier);
        Assert.Equal("cheap", cheapTier);

        Assert.Contains(balancedTier, tierAliases);
        Assert.Contains(cheapTier, tierAliases);
    }
}
