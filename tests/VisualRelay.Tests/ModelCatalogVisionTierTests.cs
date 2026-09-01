using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Vision-tier routing tests (2026-07-07 fix).  Pins the upstream model
/// strings in the template and asserts the all-vision-capable invariant
/// for the chain and selectable lists.
/// </summary>
public sealed class ModelCatalogVisionTierTests
{
    /// <summary>Models known to be vision-capable in the current config.</summary>
    private static readonly HashSet<string> VisionCapableModels =
        ["hf-qwen3-vl-235b", "hf-qwen3-vl-30b", "deepseek-v4-flash-vision-exp"];

    // ── 1. Template model strings ────────────────────────────────────────

    /// <summary>
    /// The 235B vision route is auto-routed: no provider is pinned, so Hugging
    /// Face picks the serving host. That is why its price and window are
    /// recorded at the worst case.
    /// </summary>
    [Fact]
    public void VisionRoute_Vl235bModelString_IsAutoRouted()
    {
        var upstream = ModelCatalogTestHelpers.UpstreamModel("hf-qwen3-vl-235b");

        Assert.Equal("Qwen/Qwen3-VL-235B-A22B-Instruct", upstream);
        Assert.DoesNotContain(":", upstream!, StringComparison.Ordinal);
    }

    /// <summary>The 30B vision route is auto-routed for the same reason.</summary>
    [Fact]
    public void VisionRoute_Vl30bModelString_IsAutoRouted()
    {
        var upstream = ModelCatalogTestHelpers.UpstreamModel("hf-qwen3-vl-30b");

        Assert.Equal("Qwen/Qwen3-VL-30B-A3B-Instruct", upstream);
        Assert.DoesNotContain(":", upstream!, StringComparison.Ordinal);
    }

    // ── 3. Chain exact membership ────────────────────────────────────────

    [Fact]
    public void VisionChain_HasExactMembership()
    {
        Assert.True(ModelCatalog.Chains.TryGetValue("vision", out var chain));
        var models = chain.Select(c => c.Model).ToHashSet();

        Assert.Equal(VisionCapableModels, models);

        // The chain spans two providers now, so each model carries its own key
        // rather than the tier having one. The order matters: the VL models lead
        // and the DeepSeek route is the tail, so nobody's routing changes while
        // a key that backs a leader is present.
        Assert.Equal(
            ["hf-qwen3-vl-235b", "hf-qwen3-vl-30b", "deepseek-v4-flash-vision-exp"],
            chain.Select(c => c.Model));
        Assert.Equal(
            ["HF_TOKEN", "HF_TOKEN", "DEEPSEEK_API_KEY"],
            chain.Select(c => c.RequiredKey));
    }

    // ── 4. Selectable exact membership ────────────────────────────────────

    [Fact]
    public void VisionSelectable_HasExactMembership()
    {
        Assert.True(
            ModelCatalog.SelectableModelsByTier.TryGetValue("vision", out var selectable));

        Assert.Equal(VisionCapableModels, selectable.ToHashSet());

        // No text models (kimi-k2 was the offender).
        Assert.DoesNotContain("kimi-k2", selectable);
    }

    // ── 5. Generated vision fallback chain — vision-only ──────────────────

    [Fact]
    public void VisionFallbackChain_OnlyVisionModels_WithHfToken()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var aliases = ModelCatalogTestHelpers.GeneratedAliases(present);
        var fallbacks = ModelCatalogTestHelpers.GeneratedFallbacks(present);

        Assert.True(aliases.ContainsKey("vision"));
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);

        Assert.True(fallbacks.ContainsKey("vision"));
        var chain = fallbacks["vision"];

        // Every model in the fallback chain must be vision-capable.
        Assert.All(chain, m => Assert.Contains(m, VisionCapableModels));

        // Must NOT contain any non-vision model or the fallback alias.
        Assert.DoesNotContain("kimi-k2", chain);
        Assert.DoesNotContain("fallback", chain);
        Assert.DoesNotContain("hf-qwen3-coder-next", chain);
        Assert.DoesNotContain("deepseek-v4-pro", chain);
        Assert.DoesNotContain("deepseek-v4-flash", chain);
        // Absent here only because this case supplies no DEEPSEEK_API_KEY. It
        // IS in the vision chain, as its tail; with the key present it appears.
        Assert.DoesNotContain("deepseek-v4-flash-vision-exp", chain);
        // Vision-capable or not, the frontier primary is not a vision route: GLM
        // 5.3 Flash does take images, but the vision chain stays the two VL
        // models the tier is sized and priced around.
        Assert.DoesNotContain("glm-5.3-flash", chain);
        Assert.DoesNotContain("hf-glm-5.3-flash", chain);
    }

    // ── 6. Vision alias resolves to 235B primary ──────────────────────────

    [Fact]
    public void VisionAlias_IsHfQwen3Vl235b_WhenHfTokenPresent()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var aliases = ModelCatalogTestHelpers.GeneratedAliases(present);

        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);

        // Also true with additional keys present.
        var trio = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY" };
        var trioAliases = ModelCatalogTestHelpers.GeneratedAliases(trio);
        Assert.Equal("hf-qwen3-vl-235b", trioAliases["vision"]);
    }

    // ── 7. Vision tier absent when no HF_TOKEN ────────────────────────────

    [Fact]
    public void VisionTier_AbsentWhenNoKeyBacksAVisionModel()
    {
        // The tier is omitted rather than degraded, so a vision request produces
        // a "model not found" error instead of a silent text-model answer. With
        // no key at all, no tier resolves; the stage fails naming the key that
        // would fix it.
        var noKeys = new HashSet<string>();
        Assert.Empty(ModelCatalogTestHelpers.GeneratedAliases(noKeys));

        // Moonshot backs no vision model, so the tier is still omitted.
        var moonshotOnly = new HashSet<string> { "MOONSHOT_API_KEY" };
        var msAliases = ModelCatalogTestHelpers.GeneratedAliases(moonshotOnly);
        Assert.False(msAliases.ContainsKey("vision"));
    }

    /// <summary>
    /// A DeepSeek-only install gets a working vision tier. It used to get none:
    /// the tier was HF-only, so with no HF_TOKEN it was omitted entirely and the
    /// visual-review stage could not run at all, even though a vision-capable
    /// DeepSeek model was already serving that install's cheap tier.
    ///
    /// <para>The exclusion was introduced on 2026-08-31 for one stated reason:
    /// the proxy in front of the agent stripped image parts on the DeepSeek
    /// route, so an image sent there was silently lost. That proxy was deleted
    /// the next day and image parts now reach the provider verbatim, so the
    /// reason is gone.</para>
    /// </summary>
    [Fact]
    public void VisionTier_ResolvesOnADeepSeekOnlyInstall()
    {
        var dsOnly = new HashSet<string> { "DEEPSEEK_API_KEY" };

        var aliases = ModelCatalogTestHelpers.GeneratedAliases(dsOnly);

        Assert.True(aliases.ContainsKey("vision"), "a vision-capable key must yield a vision tier");
        Assert.Equal("deepseek-v4-flash-vision-exp", aliases["vision"]);
    }
}
