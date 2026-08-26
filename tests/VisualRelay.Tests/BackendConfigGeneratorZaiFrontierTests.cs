using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// The frontier tier runs GLM 5.3 Flash. It reaches the model on Z.AI's
/// first-party API when <c>ZAI_API_KEY</c> is configured, and over Hugging Face
/// Inference Providers (provider-pinned back to Z.AI) when it is not — so a user
/// holding only an HF token still gets the same upstream model, just via another
/// host.
/// </summary>
public sealed class BackendConfigGeneratorZaiFrontierTests
{
    [Fact]
    public void Frontier_ResolvesToGlm53Flash_WhenZaiKeyPresent()
    {
        var present = new HashSet<string> { "ZAI_API_KEY", "HF_TOKEN" };

        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("glm-5.3-flash", aliases["frontier"]);

        // The HF route does not disappear — it demotes to the first fallback, so
        // a Z.AI outage still lands on the same model through another host.
        Assert.Equal("hf-glm-5.3-flash", fallbacks["frontier"][0]);
        Assert.True(BackendConfigGeneratorTestHelpers.ChainTerminatesInFallback("frontier", fallbacks));
    }

    [Fact]
    public void Frontier_FallsBackToHfRoute_WhenZaiKeyAbsent()
    {
        var present = new HashSet<string> { "HF_TOKEN" };

        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("hf-glm-5.3-flash", aliases["frontier"]);

        // A model whose key is absent must never appear anywhere in the chain,
        // or litellm burns an auth-error round trip on every frontier call.
        Assert.DoesNotContain("glm-5.3-flash", aliases.Values);
        Assert.DoesNotContain("glm-5.3-flash", fallbacks["frontier"]);
    }

    [Fact]
    public void Frontier_TierRow_ReportsZaiAsTheProvider()
    {
        var rows = BackendConfigGenerator.GetTierRows(
            new HashSet<string> { "ZAI_API_KEY", "HF_TOKEN" });

        var frontier = rows.Single(r => r.Tier == "frontier");
        Assert.Equal("glm-5.3-flash", frontier.Model);
        Assert.Equal("Z.AI", frontier.ProviderName);
        Assert.True(frontier.KeyPresent);

        Assert.Equal("Z.AI", BackendConfigGenerator.ProviderFor("glm-5.3-flash"));
        Assert.Equal("Hugging Face", BackendConfigGenerator.ProviderFor("hf-glm-5.3-flash"));
    }

    [Fact]
    public void Frontier_TierRow_ReportsHuggingFace_WhenZaiKeyAbsent()
    {
        var rows = BackendConfigGenerator.GetTierRows(new HashSet<string> { "HF_TOKEN" });

        var frontier = rows.Single(r => r.Tier == "frontier");
        Assert.Equal("hf-glm-5.3-flash", frontier.Model);
        Assert.Equal("Hugging Face", frontier.ProviderName);
    }

    /// <summary>
    /// Z.AI's published GLM-5.3-Flash rates (docs.z.ai/guides/overview/pricing,
    /// 2026-08-26): $0.15 input, $0.03 cached input, $0.50 output per 1M tokens.
    /// Z.AI is running a 50%-off promotion on this model until 2026-09-09, and the
    /// sticker rate is what is recorded here on purpose — the same call the
    /// claude-sonnet entry makes — so estimates do not under-count once it lapses.
    /// </summary>
    [Fact]
    public void Glm53Flash_PricesAtZaiStickerRates()
    {
        var flash = RelayPricing.Default["glm-5.3-flash"];

        Assert.Equal(0.15, flash.Input);
        Assert.Equal(0.50, flash.Output);
        Assert.Equal(0.03, flash.EffectiveCachedInput);
        // Z.AI publishes no separate cache-write rate, so it falls back to input.
        Assert.Equal(0.15, flash.EffectiveCacheWrite);
    }

    /// <summary>
    /// Both routes serve the same upstream model, so they must price the same. If
    /// they ever diverge, a Z.AI outage would silently change what a run costs and
    /// the estimates need revisiting alongside the rate edit.
    /// </summary>
    [Fact]
    public void BothGlm53FlashRoutes_PriceIdentically()
    {
        var zai = RelayPricing.Default["glm-5.3-flash"];
        var hf = RelayPricing.Default["hf-glm-5.3-flash"];

        Assert.Equal(zai.Input, hf.Input);
        Assert.Equal(zai.Output, hf.Output);
        Assert.Equal(zai.EffectiveCachedInput, hf.EffectiveCachedInput);
        Assert.Equal(zai.EffectiveCacheWrite, hf.EffectiveCacheWrite);
    }

    /// <summary>
    /// The retired GLM 5.2 and GLM 5.3 entries must be gone everywhere at once: a
    /// name left in the pricing table or a selectable list outlives the
    /// <c>model_list</c> route it needs, and resolves to a model the proxy cannot
    /// dispatch.
    /// </summary>
    [Fact]
    public void RetiredGlmModelNames_AreGoneFromPricingAndSelectableLists()
    {
        string[] retired = ["glm-5.2", "glm-5.3"];

        foreach (var model in retired)
        {
            Assert.DoesNotContain(model, RelayPricing.Default.Keys);
            Assert.DoesNotContain(model, BackendConfigGenerator.SelectableModelsByTier.Values.SelectMany(m => m));
            Assert.DoesNotContain(model, BackendConfigGenerator.Chains.Values.SelectMany(c => c).Select(c => c.Model));
        }
    }

    /// <summary>
    /// <see cref="BackendConfigGenerator.ProviderKeyNames"/> is the single list the
    /// backend probes for present keys; the settings panel keeps its own rows for
    /// display names and sign-up URLs. A provider added to one and not the other
    /// is either unprobed (never resolves) or unsettable (no UI to paste a key).
    /// Membership must match; the two orderings are independent (the panel leads
    /// with Hugging Face because that key gates runs).
    /// </summary>
    [Fact]
    public void ProviderKeyNames_MatchTheSettingsPanelRows()
    {
        var probed = BackendConfigGenerator.ProviderKeyNames.ToHashSet(StringComparer.Ordinal);
        var displayed = App.ViewModels.MainWindowViewModel.AllProviderKeys
            .Select(r => r.EnvVarName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(probed, displayed);
    }
}
