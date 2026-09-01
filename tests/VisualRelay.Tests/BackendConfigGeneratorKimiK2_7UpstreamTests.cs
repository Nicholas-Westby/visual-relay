using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

// The 'K2_7' segment encodes the Kimi K2.7 model id under test; the underscore
// is a deliberate, meaningful part of the name, not a naming-convention slip.
// ReSharper disable once InconsistentNaming
public sealed class BackendConfigGeneratorKimiK2_7UpstreamTests
{
    // ── Kimi K2.7 Code upstream model id ─────────────────────────────────

    /// <summary>
    /// The kimi-k2 alias points at the Kimi K2.7 Code upstream model
    /// (kimi-k2.7-code, released 2026-06-12), not the older K2.6.
    /// <para>
    /// This used to read the proxy's YAML template. The catalog is the source
    /// of truth now, so it reads the route.
    /// </para>
    /// </summary>
    [Fact]
    public void KimiK2_UpstreamModel_IsKimiK2_7Code()
    {
        var upstream = BackendConfigGeneratorTestHelpers.UpstreamModel("kimi-k2");

        Assert.NotNull(upstream);
        Assert.Contains("kimi-k2.7-code", upstream, StringComparison.Ordinal);
    }

    /// <summary>
    /// With MOONSHOT_API_KEY present, a tier that resolves to kimi-k2 reaches
    /// the K2.7 upstream. The alias and the id it sends are separate things, and
    /// this is what couples them.
    /// </summary>
    [Fact]
    public void KimiK2_WhenChained_ReachesTheK2_7Upstream()
    {
        var present = new HashSet<string> { "HF_TOKEN", "MOONSHOT_API_KEY" };
        var chains = BackendConfigGeneratorTestHelpers.GeneratedAliases(present)
            .Values.Concat(
                BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present).Values.SelectMany(v => v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("kimi-k2", chains);
        Assert.Equal("kimi-k2.7-code", ProviderRoutes.For("kimi-k2")!.UpstreamModel);
    }

    /// <summary>No route may still point at the retired K2.6 model id.</summary>
    [Fact]
    public void NoRoute_StillPointsAtK2_6()
    {
        var stale = ProviderRoutes.Aliases
            .Where(alias => ProviderRoutes.For(alias)!.UpstreamModel
                .Contains("kimi-k2.6", StringComparison.Ordinal))
            .ToList();

        Assert.True(stale.Count == 0, "routes still on the retired K2.6: " + string.Join(", ", stale));
    }
}
