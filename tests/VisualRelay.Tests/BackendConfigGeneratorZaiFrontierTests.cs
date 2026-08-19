using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// The frontier tier defaults to GLM 5.3 on Z.AI's first-party API when
/// <c>ZAI_API_KEY</c> is configured, and falls back to GLM 5.2 over Hugging
/// Face when it is not. A user who never adds a Z.AI key must see byte-identical
/// behaviour to before the 5.3 upgrade.
/// </summary>
public sealed class BackendConfigGeneratorZaiFrontierTests
{
    [Fact]
    public void Frontier_ResolvesToGlm53_WhenZaiKeyPresent()
    {
        var present = new HashSet<string> { "ZAI_API_KEY", "HF_TOKEN" };

        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("glm-5.3", aliases["frontier"]);

        // GLM 5.2 does not disappear — it demotes to the first fallback, so a
        // Z.AI outage still lands on the model that used to be the primary.
        Assert.Equal("glm-5.2", fallbacks["frontier"][0]);
        Assert.True(BackendConfigGeneratorTestHelpers.ChainTerminatesInFallback("frontier", fallbacks));
    }

    [Fact]
    public void Frontier_FallsBackToGlm52_WhenZaiKeyAbsent()
    {
        var present = new HashSet<string> { "HF_TOKEN" };

        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("glm-5.2", aliases["frontier"]);

        // A model whose key is absent must never appear anywhere in the chain,
        // or litellm burns an auth-error round trip on every frontier call.
        Assert.DoesNotContain("glm-5.3", aliases.Values);
        Assert.DoesNotContain("glm-5.3", fallbacks["frontier"]);
    }

    [Fact]
    public void Frontier_TierRow_ReportsZaiAsTheProvider()
    {
        var rows = BackendConfigGenerator.GetTierRows(
            new HashSet<string> { "ZAI_API_KEY", "HF_TOKEN" });

        var frontier = rows.Single(r => r.Tier == "frontier");
        Assert.Equal("glm-5.3", frontier.Model);
        Assert.Equal("Z.AI", frontier.ProviderName);
        Assert.True(frontier.KeyPresent);

        Assert.Equal("Z.AI", BackendConfigGenerator.ProviderFor("glm-5.3"));
        Assert.Equal("Hugging Face", BackendConfigGenerator.ProviderFor("glm-5.2"));
    }

    [Fact]
    public void Frontier_TierRow_ReportsHuggingFace_WhenZaiKeyAbsent()
    {
        var rows = BackendConfigGenerator.GetTierRows(new HashSet<string> { "HF_TOKEN" });

        var frontier = rows.Single(r => r.Tier == "frontier");
        Assert.Equal("glm-5.2", frontier.Model);
        Assert.Equal("Hugging Face", frontier.ProviderName);
    }

    /// <summary>
    /// Z.AI publishes identical rates for GLM 5.3 and GLM 5.2
    /// (docs.z.ai/guides/overview/pricing, 2026-08-19), so promoting 5.3 to the
    /// frontier default is cost-neutral. If Z.AI ever diverges the two, this
    /// fails and the run-cost estimates need revisiting alongside the rate edit.
    /// </summary>
    [Fact]
    public void Glm53_And_Glm52_PriceIdentically()
    {
        var glm53 = RelayPricing.Default["glm-5.3"];
        var glm52 = RelayPricing.Default["glm-5.2"];

        Assert.Equal(glm52.Input, glm53.Input);
        Assert.Equal(glm52.Output, glm53.Output);
        Assert.Equal(glm52.EffectiveCachedInput, glm53.EffectiveCachedInput);
        Assert.Equal(glm52.EffectiveCacheWrite, glm53.EffectiveCacheWrite);
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
