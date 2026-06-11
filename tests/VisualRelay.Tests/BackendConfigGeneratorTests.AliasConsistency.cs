using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class BackendConfigGeneratorTests
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
        var swivalModelValues = ParseSwivalProfileModelValues(SwivalProfileSession.DefaultToml);

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

    /// <summary>
    /// Extracts <c>model = "…"</c> values from a swival.toml profile string,
    /// keyed by profile name (e.g. <c>"balanced"</c> → <c>"balanced"</c>).
    /// </summary>
    private static Dictionary<string, string> ParseSwivalProfileModelValues(string toml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentProfile = null;

        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("[profiles.", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                currentProfile = line["[profiles.".Length..^1];
            }
            else if (currentProfile != null && line.StartsWith("model = \"", StringComparison.Ordinal))
            {
                var closeQuote = line.IndexOf('"', "model = \"".Length);
                if (closeQuote >= 0)
                {
                    var model = line["model = \"".Length..closeQuote];
                    result[currentProfile] = model;
                }

                currentProfile = null; // consume the model line
            }
        }

        return result;
    }
}
