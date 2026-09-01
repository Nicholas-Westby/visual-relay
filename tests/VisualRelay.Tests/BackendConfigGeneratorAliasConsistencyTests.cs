using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

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
    /// </summary>
    [Fact]
    public void TierAliasNames_AreConsistentAcrossBackendConfigAndPricing()
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

    }
}
