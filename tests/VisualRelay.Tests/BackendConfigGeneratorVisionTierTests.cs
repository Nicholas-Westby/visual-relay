using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Vision-tier routing tests (2026-07-07 fix).  Pins the upstream model
/// strings in the template and asserts the all-vision-capable invariant
/// for the chain and selectable lists.
/// </summary>
public sealed class BackendConfigGeneratorVisionTierTests
{
    /// <summary>Models known to be vision-capable in the current config.</summary>
    private static readonly HashSet<string> VisionCapableModels =
        ["hf-qwen3-vl-235b", "hf-qwen3-vl-30b"];

    // ── 1. Template model strings ────────────────────────────────────────

    [Fact]
    public void VisionTemplate_Vl235bModelString_IsAutoRouted()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        // The 235B entry must use the auto-routed path (no pinned provider
        // like /novita/ or /deepinfra/ in the segment).
        Assert.Contains(
            "model: huggingface/Qwen/Qwen3-VL-235B-A22B-Instruct",
            yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("huggingface/novita/Qwen/Qwen3-VL-235B", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("huggingface/deepinfra/Qwen/Qwen3-VL-235B", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void VisionTemplate_Vl30bModelString_IsAutoRouted()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        Assert.Contains(
            "model: huggingface/Qwen/Qwen3-VL-30B-A3B-Instruct",
            yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("huggingface/novita/Qwen/Qwen3-VL-30B", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("huggingface/deepinfra/Qwen/Qwen3-VL-30B", yaml, StringComparison.Ordinal);
    }

    // ── 3. Chain exact membership ────────────────────────────────────────

    [Fact]
    public void VisionChain_HasExactMembership()
    {
        Assert.True(BackendConfigGenerator.Chains.TryGetValue("vision", out var chain));
        var models = chain.Select(c => c.Model).ToHashSet();

        Assert.Equal(VisionCapableModels, models);

        // Every model in the vision chain requires HF_TOKEN (the sole key
        // gating HF Inference Providers).
        Assert.All(chain, c => Assert.Equal("HF_TOKEN", c.RequiredKey));
    }

    // ── 4. Selectable exact membership ────────────────────────────────────

    [Fact]
    public void VisionSelectable_HasExactMembership()
    {
        Assert.True(
            BackendConfigGenerator.SelectableModelsByTier.TryGetValue("vision", out var selectable));

        Assert.Equal(VisionCapableModels, selectable.ToHashSet());

        // No text models (kimi-k2 was the offender).
        Assert.DoesNotContain("kimi-k2", selectable);
    }

    // ── 5. Generated vision fallback chain — vision-only ──────────────────

    [Fact]
    public void VisionFallbackChain_OnlyVisionModels_WithHfToken()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

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
        Assert.DoesNotContain("glm-5.2", chain);
    }

    // ── 6. Vision alias resolves to 235B primary ──────────────────────────

    [Fact]
    public void VisionAlias_IsHfQwen3Vl235b_WhenHfTokenPresent()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);

        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);

        // Also true with additional keys present.
        var trio = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY" };
        var trioAliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(trio);
        Assert.Equal("hf-qwen3-vl-235b", trioAliases["vision"]);
    }

    // ── 7. Vision tier absent when no HF_TOKEN ────────────────────────────

    [Fact]
    public void VisionTier_AbsentWhenNoHfToken()
    {
        // No HF_TOKEN → both VL models unavailable → vision tier skipped
        // entirely, so a vision request produces a "model not found" error
        // instead of a silent text-model answer.
        var noKeys = new HashSet<string>();
        var noKeysAliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(noKeys);
        Assert.False(noKeysAliases.ContainsKey("vision"));

        var dsOnly = new HashSet<string> { "DEEPSEEK_API_KEY" };
        var dsAliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(dsOnly);
        Assert.False(dsAliases.ContainsKey("vision"));

        var moonshotOnly = new HashSet<string> { "MOONSHOT_API_KEY" };
        var msAliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(moonshotOnly);
        Assert.False(msAliases.ContainsKey("vision"));
    }
}
