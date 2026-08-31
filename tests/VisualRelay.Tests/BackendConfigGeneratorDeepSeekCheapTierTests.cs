using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// The cheap tier runs DeepSeek V4 Flash Vision Exp: same context, same rates
/// and same peak schedule as the text-only V4 Flash it takes over from. The
/// "exp" model is experimental and DeepSeek can withdraw it without notice, so
/// the text-only Flash is demoted rather than dropped: it sits directly behind
/// as the first fallback, where a withdrawal costs one failed round trip instead
/// of the whole tier. Image input does not survive litellm's DeepSeek route, so
/// this is a text tier in practice — see the model_list entry in
/// <c>litellm-config.yaml</c>.
/// </summary>
public sealed class BackendConfigGeneratorDeepSeekCheapTierTests
{
    [Fact]
    public void Cheap_ResolvesToFlashVisionExp_WhenDeepSeekKeyPresent()
    {
        var present = new HashSet<string> { "DEEPSEEK_API_KEY", "HF_TOKEN" };

        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("deepseek-v4-flash-vision-exp", aliases["cheap"]);

        // Demoted, not removed: the text-only Flash is the first fallback.
        Assert.Equal("deepseek-v4-flash", fallbacks["cheap"][0]);
        Assert.True(BackendConfigGeneratorTestHelpers.ChainTerminatesInFallback("cheap", fallbacks));
    }

    [Fact]
    public void Cheap_TierRow_ReportsDeepSeekAsTheProvider()
    {
        var rows = BackendConfigGenerator.GetTierRows(
            new HashSet<string> { "DEEPSEEK_API_KEY", "HF_TOKEN" });

        var cheap = rows.Single(r => r.Tier == "cheap");
        Assert.Equal("deepseek-v4-flash-vision-exp", cheap.Model);
        Assert.Equal("DeepSeek", cheap.ProviderName);
        Assert.True(cheap.KeyPresent);

        Assert.Equal("DeepSeek", BackendConfigGenerator.ProviderFor("deepseek-v4-flash-vision-exp"));
    }

    /// <summary>
    /// Both Flash routes stay in the cheap picker. A saved
    /// <c>tierModelOverrides</c> entry is silently dropped by
    /// <c>RelayConfigLoader</c> once its model leaves the selectable list, so
    /// removing the demoted model would reset every user who had pinned it.
    /// </summary>
    [Fact]
    public void BothFlashRoutes_AreSelectableForTheCheapTier()
    {
        var cheap = BackendConfigGenerator.SelectableModelsByTier["cheap"];

        Assert.Equal("deepseek-v4-flash-vision-exp", cheap[0]);
        Assert.Contains("deepseek-v4-flash", cheap);
    }

    /// <summary>
    /// DeepSeek's published rates (api-docs.deepseek.com/quick_start/pricing,
    /// 2026-08-31): the vision model bills at the text-only Flash rates of $0.22
    /// input, $0.66 output and $0.007 cached input per 1M tokens. Images are
    /// converted to input tokens and billed as input, so no separate image rate
    /// is needed. Equal rates are what make the demotion free.
    /// </summary>
    [Fact]
    public void FlashVisionExp_PricesAtTheTextOnlyFlashRates()
    {
        var vision = RelayPricing.Default["deepseek-v4-flash-vision-exp"];
        var text = RelayPricing.Default["deepseek-v4-flash"];

        Assert.Equal(0.22, vision.Input);
        Assert.Equal(0.66, vision.Output);
        Assert.Equal(0.007, vision.EffectiveCachedInput);
        Assert.Equal(0.22, vision.EffectiveCacheWrite);

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
    /// The proxy dispatches on the first-party DeepSeek route, at the same 75s
    /// ceiling as its sibling. That ceiling is what trips the fallback chain
    /// before the relay's cheap-tier first-output watchdog kills swival on the
    /// provider that is already hanging.
    /// </summary>
    [Fact]
    public void FlashVisionExp_RoutesToDeepSeekAtTheSharedFlashTimeout()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        Assert.Equal(
            "deepseek/deepseek-v4-flash-vision-exp",
            BackendConfigGeneratorTestHelpers.ParseUpstreamModel(yaml, "deepseek-v4-flash-vision-exp"));

        var timeouts = BackendConfigGeneratorTestHelpers.ParseModelTimeouts(yaml);
        Assert.Equal(timeouts["deepseek-v4-flash"], timeouts["deepseek-v4-flash-vision-exp"]);
    }
}
