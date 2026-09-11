using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// The cheap tier runs DeepSeek V4.1 Flash, and V4 Flash sits directly behind it
/// as the only fallback before the HF floor. That name is served by V4.1 Flash
/// upstream since 2026-09-10, so it is priced at the same rates on the same peak
/// schedule and a hop between them costs only the attempts it spends. It is kept
/// because a routed-away name can be withdrawn without notice; the other two V4
/// aliases were dropped on 2026-09-11 rather than queued behind it. Images DO
/// reach the model on the direct path, but the vision tier is still the one
/// sized and priced for image work.
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

        // Demoted, not removed: the surviving V4 name is the first fallback.
        Assert.Equal("deepseek-v4-flash", fallbacks["cheap"][0]);
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
        Assert.Equal("DeepSeek", ModelCatalog.ProviderFor("deepseek-v4-flash"));
    }

    /// <summary>
    /// Both surviving Flash routes stay in the cheap picker, the new default
    /// first. A saved <c>tierModelOverrides</c> entry is silently dropped by
    /// <c>RelayConfigLoader</c> once its model leaves the selectable list, so
    /// removing the demoted model would reset every user who had pinned it.
    /// </summary>
    [Fact]
    public void BothFlashRoutes_AreSelectableForTheCheapTier()
    {
        var cheap = ModelCatalog.SelectableModelsByTier["cheap"];

        Assert.Equal("deepseek-flash", cheap[0]);
        Assert.Contains("deepseek-v4-flash", cheap);
    }

    /// <summary>
    /// DeepSeek's published rates (api-docs.deepseek.com/quick_start/pricing,
    /// 2026-09-10): the V4 Flash name was retired that day and routed to V4.1
    /// Flash, so it bills at the V4.1 rates of $0.15 input, $0.60 output and
    /// $0.003 cached input per 1M tokens. Images are converted to input tokens
    /// and billed as input, so no separate image rate is needed. Equal rates are
    /// what make the demotion free.
    /// </summary>
    [Fact]
    public void V4Flash_PricesAtTheV41FlashRates()
    {
        var legacy = RelayPricing.Default["deepseek-v4-flash"];
        var head = RelayPricing.Default["deepseek-flash"];

        Assert.Equal(0.15, legacy.Input);
        Assert.Equal(0.60, legacy.Output);
        Assert.Equal(0.003, legacy.EffectiveCachedInput);
        Assert.Equal(0.15, legacy.EffectiveCacheWrite);

        Assert.Equal(head.Input, legacy.Input);
        Assert.Equal(head.Output, legacy.Output);
        Assert.Equal(head.EffectiveCachedInput, legacy.EffectiveCachedInput);
        Assert.Equal(head.EffectiveCacheWrite, legacy.EffectiveCacheWrite);
    }

    /// <summary>
    /// Peak hours double both Flash models over the same windows, so failing
    /// over between them never changes what an hour of running costs.
    /// </summary>
    [Fact]
    public void V4Flash_SharesTheDeepSeekPeakWindows()
    {
        var legacy = RelayPricing.Default["deepseek-v4-flash"];
        var head = RelayPricing.Default["deepseek-flash"];

        Assert.NotNull(legacy.Windows);
        Assert.Equal<IEnumerable<RateWindow>>(head.Windows!, legacy.Windows!);
    }

    /// <summary>
    /// The V4 Flash route dispatches to DeepSeek on the same budgets as the head
    /// it falls back from. Those budgets are what trip the fallback chain before
    /// the stage watchdog fires on a provider that is already hanging.
    /// </summary>
    [Fact]
    public void V4Flash_RoutesToDeepSeekOnTheSharedFlashBudgets()
    {
        Assert.Equal(
            "deepseek-v4-flash",
            ModelCatalogTestHelpers.UpstreamModel("deepseek-v4-flash"));

        var timeouts = ModelCatalogTestHelpers.ModelTimeouts();
        Assert.Equal(timeouts["deepseek-flash"], timeouts["deepseek-v4-flash"]);
    }
}
