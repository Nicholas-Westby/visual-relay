using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// DeepSeek V4.1 Flash leads the cheap, balanced and (on a DeepSeek-only
/// install) vision tiers. It is a million-token model at a quarter of the old
/// Flash input rate. DeepSeek has served V4 Flash from it since 2026-09-10, so
/// the one hop behind the head is the same weights; it is kept because a retired
/// name can be withdrawn at any time, and two failed attempts are cheaper than
/// losing the tier. The other two V4 aliases were dropped on 2026-09-11.
/// </summary>
public sealed class ModelCatalogDeepSeekFlashTests
{
    private static readonly string[] LegacyDeepSeekModels = ["deepseek-v4-flash"];

    private static HashSet<string> AllKeys() => new(StringComparer.Ordinal)
    {
        "DEEPSEEK_API_KEY", "HF_TOKEN", "MOONSHOT_API_KEY", "ZAI_API_KEY",
    };

    /// <summary>
    /// The alias is the id DeepSeek answers to, so the served model it echoes
    /// back is priced at its own row rather than resolved through a tier.
    /// </summary>
    [Fact]
    public void DeepSeekFlash_RoutesToDeepSeekUnderItsOwnUpstreamId()
    {
        var route = ProviderRoutes.For("deepseek-flash");

        Assert.NotNull(route);
        Assert.Equal("DeepSeek", route.ProviderName);
        Assert.Equal("deepseek-flash", route.UpstreamModel);
        Assert.Equal("DEEPSEEK_API_KEY", route.ApiKeyEnvVar);
        Assert.Equal("deepseek-flash", ProviderRoutes.AliasForServedModel("deepseek-flash"));
    }

    /// <summary>
    /// The context window is what compaction measures against, so it records the
    /// million tokens the provider actually serves rather than the 128k its
    /// predecessors were recorded at.
    /// </summary>
    [Fact]
    public void DeepSeekFlash_RecordsTheMillionTokenWindowOnFastBudgets()
    {
        var route = ProviderRoutes.For("deepseek-flash")!;

        Assert.Equal(1_000_000, route.ContextWindow);
        Assert.Equal(ProviderRoutes.For("deepseek-v4-flash")!.Timeouts, route.Timeouts);
    }

    /// <summary>
    /// Published rates (api-docs.deepseek.com/quick_start/pricing, 2026-09-10),
    /// off-peak base: cache miss $0.15, output $0.60, cache hit $0.003 per 1M
    /// tokens. Cache write equals input because DeepSeek charges nothing extra
    /// to populate the cache.
    /// </summary>
    [Fact]
    public void DeepSeekFlash_PricesAtThePublishedOffPeakRates()
    {
        var flash = RelayPricing.Default["deepseek-flash"];

        Assert.Equal(0.15, flash.Input);
        Assert.Equal(0.60, flash.Output);
        Assert.Equal(0.003, flash.EffectiveCachedInput);
        Assert.Equal(0.15, flash.EffectiveCacheWrite);
    }

    /// <summary>
    /// The peak schedule is unchanged from its siblings, so a hop between
    /// DeepSeek models never changes what an hour of running costs.
    /// </summary>
    [Fact]
    public void DeepSeekFlash_SharesTheDeepSeekPeakWindows()
    {
        var flash = RelayPricing.Default["deepseek-flash"];
        var sibling = RelayPricing.Default["deepseek-v4-flash"];

        Assert.NotNull(flash.Windows);
        Assert.Equal<IEnumerable<RateWindow>>(sibling.Windows!, flash.Windows!);
    }

    /// <summary>
    /// The surviving V4 name is served by V4.1 Flash upstream, so it is billed
    /// at its rates. Pricing it at its withdrawn sticker rates would over-count
    /// every fallback hop by up to four times.
    /// </summary>
    [Fact]
    public void TheLegacyDeepSeekRows_ArePricedAsV41Flash()
    {
        var flash = RelayPricing.Default["deepseek-flash"];

        foreach (var legacy in LegacyDeepSeekModels)
        {
            var priced = RelayPricing.Default[legacy];

            Assert.Equal(flash.Input, priced.Input);
            Assert.Equal(flash.Output, priced.Output);
            Assert.Equal(flash.EffectiveCachedInput, priced.EffectiveCachedInput);
            Assert.Equal(flash.EffectiveCacheWrite, priced.EffectiveCacheWrite);
        }
    }

    /// <summary>
    /// The cheap chain leads with V4.1 Flash and keeps one V4 name behind it, so
    /// a withdrawn head costs one round trip rather than the tier — without
    /// spending eight attempts on DeepSeek before any other provider is tried.
    /// </summary>
    [Fact]
    public void CheapChain_LeadsWithFlashAndKeepsOneV4HopBehindIt()
    {
        var chain = ModelCatalog.ResolveChains(AllKeys())["cheap"];

        Assert.Equal(
            ["deepseek-flash", "deepseek-v4-flash", ModelCatalog.FallbackFloorModel],
            chain);
    }

    /// <summary>
    /// Balanced leads with the same model and keeps Kimi as its second provider,
    /// so a DeepSeek outage still leaves the tier a different upstream to reach.
    /// </summary>
    [Fact]
    public void BalancedChain_LeadsWithFlashAndStillDiversifiesAtKimi()
    {
        var chain = ModelCatalog.ResolveChains(AllKeys())["balanced"];

        Assert.Equal(
            [
                "deepseek-flash", "kimi-k2", "deepseek-v4-flash",
                ModelCatalog.FallbackFloorModel,
            ],
            chain);
    }

    /// <summary>
    /// Frontier reaches no DeepSeek model at all, and vision ends on V4.1 Flash
    /// so a DeepSeek-only install keeps a tier that can read an image.
    /// </summary>
    [Fact]
    public void FrontierHasNoDeepSeekHop_AndVisionEndsOnFlash()
    {
        var chains = ModelCatalog.ResolveChains(AllKeys());

        Assert.Equal("glm-5.3-flash", chains["frontier"][0]);
        Assert.DoesNotContain(chains["frontier"], m => m.StartsWith("deepseek", StringComparison.Ordinal));
        Assert.Equal("deepseek-flash", chains["vision"][^1]);
    }

    /// <summary>
    /// The picker leads with the default in both tiers and offers every name
    /// still routable behind it: a saved override naming a model that leaves the
    /// list is silently dropped on load, resetting whoever pinned it.
    /// </summary>
    [Fact]
    public void SelectableLists_LeadWithFlashAndKeepEveryRoutableEntry()
    {
        var cheap = ModelCatalog.SelectableModelsByTier["cheap"];
        var balanced = ModelCatalog.SelectableModelsByTier["balanced"];

        Assert.Equal("deepseek-flash", cheap[0]);
        Assert.Equal("deepseek-flash", balanced[0]);

        Assert.Equal(
            ["deepseek-v4-flash", "hf-qwen3-coder-next"],
            cheap.Skip(1));
        Assert.Equal(
            ["kimi-k2", "deepseek-v4-flash", "hf-qwen3-coder-next"],
            balanced.Skip(1));
    }

    /// <summary>
    /// A DeepSeek key alone puts V4.1 Flash on both tiers, which is the whole
    /// point: the default install now runs on the cheapest million-token model
    /// DeepSeek publishes.
    /// </summary>
    [Fact]
    public void ADeepSeekOnlyInstall_ResolvesBothTiersToFlash()
    {
        var present = new HashSet<string>(StringComparer.Ordinal) { "DEEPSEEK_API_KEY", "HF_TOKEN" };

        var aliases = ModelCatalogTestHelpers.GeneratedAliases(present);

        Assert.Equal("deepseek-flash", aliases["cheap"]);
        Assert.Equal("deepseek-flash", aliases["balanced"]);
    }
}
