using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorAliasConsistencyTests
{
    // ── Cross-file alias-name consistency ────────────────────────────────

    /// <summary>
    /// The balanced/cheap tier-alias names must be identical across the
    /// three sites that define them:
    ///   <see cref="BackendConfigGenerator"/>.<c>Chains</c>.Keys,
    ///   <see cref="RelayPricing"/>.<c>Default</c>.Keys, and
    ///   <see cref="SwivalProfileSession"/>.<c>DefaultToml</c> model values.
    /// If this test fails, a tier alias was renamed in one place but not
    /// the others — update all three together.
    /// </summary>
    [Fact]
    public void TierAliasNames_AreConsistentAcrossBackendConfigPricingAndSwivalProfile()
    {
        // 1. Extract tier-alias names from BackendConfigGenerator.Chains.
        var backendAliases = BackendConfigGenerator.Chains.Keys.ToHashSet(StringComparer.Ordinal);

        // 2. Extract tier-alias names from RelayPricing.Default.
        var pricingAliases = RelayPricing.Default.Keys.ToHashSet(StringComparer.Ordinal);

        // 3. Parse SwivalProfileSession.DefaultToml for model values of
        //    the [profiles.balanced] and [profiles.cheap] sections.
        var swivalModelValues = BackendConfigGeneratorTestHelpers.ParseSwivalProfileModelValues(
            SwivalProfileSession.DefaultToml);

        // The balanced and cheap model values are the canonical tier names.
        var balancedTier = swivalModelValues["balanced"];
        var cheapTier = swivalModelValues["cheap"];

        // Vestigial "-kimi" suffix must not appear in the tier names.
        Assert.DoesNotContain("-kimi", balancedTier, StringComparison.Ordinal);
        Assert.DoesNotContain("-kimi", cheapTier, StringComparison.Ordinal);
        Assert.Equal("balanced", balancedTier);
        Assert.Equal("cheap", cheapTier);

        // The canonical tier names must be present in all three sources.
        Assert.Contains(balancedTier, backendAliases);
        Assert.Contains(cheapTier, backendAliases);
        Assert.Contains(balancedTier, pricingAliases);
        Assert.Contains(cheapTier, pricingAliases);
    }
}
