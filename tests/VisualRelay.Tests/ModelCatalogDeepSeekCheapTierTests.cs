using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// The cheap tier runs DeepSeek V4.1 Flash, and the V4 Flash Vision Exp that
/// used to lead it sits directly behind as the first fallback. The two V4 Flash
/// names are served by V4.1 Flash upstream since 2026-09-10 and V4 Pro follows
/// on 2026-09-14, so all three are priced at its rates on its peak schedule and
/// a hop between them costs only the attempts it spends. They are kept because a
/// routed-away name can be withdrawn without notice. Images DO reach the model
/// on the direct path, but the vision tier is still the one sized and priced for
/// image work.
/// </summary>
public sealed class ModelCatalogDeepSeekCheapTierTests
{
    [Fact]
    public void Cheap_ResolvesToV41Flash_WhenDeepSeekKeyPresent()
    {
        var present = new HashSet<string> { "DEEPSEEK_API_KEY", "HF_TOKEN" };

        var aliases = ModelCatalogTestHelpers.GeneratedAliases(present);
        var fallbacks = ModelCatalogTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("deepseek-flash", aliases["cheap"]);

        // Demoted, not removed: the former head is the first fallback.
        Assert.Equal("deepseek-v4-flash-vision-exp", fallbacks["cheap"][0]);
        Assert.Contains("deepseek-v4-flash", fallbacks["cheap"]);
        Assert.True(ModelCatalogTestHelpers.ChainTerminatesInFallback("cheap", fallbacks));
    }

    [Fact]
    public void Cheap_TierRow_ReportsDeepSeekAsTheProvider()
    {
        var rows = ModelCatalog.GetTierRows(
            new HashSet<string> { "DEEPSEEK_API_KEY", "HF_TOKEN" });

        var cheap = rows.Single(r => r.Tier == "cheap");
        Assert.Equal("deepseek-flash", cheap.Model);
        Assert.Equal("DeepSeek", cheap.ProviderName);
        Assert.True(cheap.KeyPresent);

        Assert.Equal("DeepSeek", ModelCatalog.ProviderFor("deepseek-flash"));
        Assert.Equal("DeepSeek", ModelCatalog.ProviderFor("deepseek-v4-flash-vision-exp"));
    }

    /// <summary>
    /// Both Flash routes stay in the cheap picker behind the new default. A
    /// saved <c>tierModelOverrides</c> entry is silently dropped by
    /// <c>RelayConfigLoader</c> once its model leaves the selectable list, so
    /// removing a demoted model would reset every user who had pinned it.
    /// </summary>
    [Fact]
    public void BothFlashRoutes_AreSelectableForTheCheapTier()
    {
        var cheap = ModelCatalog.SelectableModelsByTier["cheap"];

        Assert.Equal("deepseek-flash", cheap[0]);
        Assert.Contains("deepseek-v4-flash-vision-exp", cheap);
        Assert.Contains("deepseek-v4-flash", cheap);
    }

    /// <summary>
    /// DeepSeek's published rates (api-docs.deepseek.com/quick_start/pricing,
    /// 2026-09-10): the vision model is one of the names retired that day and
    /// routed to V4.1 Flash, so it bills at the V4.1 rates of $0.15 input, $0.60
    /// output and $0.003 cached input per 1M tokens. Images are converted to
    /// input tokens and billed as input, so no separate image rate is needed.
    /// Equal rates are what make the demotion free.
    /// </summary>
    [Fact]
    public void FlashVisionExp_PricesAtTheTextOnlyFlashRates()
    {
        var vision = RelayPricing.Default["deepseek-v4-flash-vision-exp"];
        var text = RelayPricing.Default["deepseek-v4-flash"];

        Assert.Equal(0.15, vision.Input);
        Assert.Equal(0.60, vision.Output);
        Assert.Equal(0.003, vision.EffectiveCachedInput);
        Assert.Equal(0.15, vision.EffectiveCacheWrite);

        Assert.Equal(text.Input, vision.Input);
        Assert.Equal(text.Output, vision.Output);
        Assert.Equal(text.EffectiveCachedInput, vision.EffectiveCachedInput);
        Assert.Equal(text.EffectiveCacheWrite, vision.EffectiveCacheWrite);
    }

    /// <summary>
    /// Peak hours double both Flash models over the same windows, so failing
    /// over between them never changes what an hour of running costs.
    /// </summary>
    [Fact]
    public void FlashVisionExp_SharesTheDeepSeekPeakWindows()
    {
        var vision = RelayPricing.Default["deepseek-v4-flash-vision-exp"];
        var text = RelayPricing.Default["deepseek-v4-flash"];

        Assert.NotNull(vision.Windows);
        Assert.Equal<IEnumerable<RateWindow>>(text.Windows!, vision.Windows!);
    }

    /// <summary>
    /// The vision-exp route dispatches to DeepSeek on the same budgets as its
    /// sibling. Those budgets are what trip the fallback chain before the stage
    /// watchdog fires on a provider that is already hanging.
    /// </summary>
    [Fact]
    public void FlashVisionExp_RoutesToDeepSeekOnTheSharedFlashBudgets()
    {
        Assert.Equal(
            "deepseek-v4-flash-vision-exp",
            ModelCatalogTestHelpers.UpstreamModel("deepseek-v4-flash-vision-exp"));

        var timeouts = ModelCatalogTestHelpers.ModelTimeouts();
        Assert.Equal(timeouts["deepseek-v4-flash"], timeouts["deepseek-v4-flash-vision-exp"]);
    }
}
