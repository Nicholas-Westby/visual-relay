using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorTests
{
    // ── 1. HF only ───────────────────────────────────────────────────────
    [Fact]
    public void HfOnly_DefaultTiersResolveToFallbackFloor()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var summary = BackendConfigGenerator.Summarize(present);

        Assert.Equal(BackendConfigGenerator.FallbackFloorModel, aliases["cheap"]);
        Assert.Equal(BackendConfigGenerator.FallbackFloorModel, aliases["balanced"]);
        // The HF route to GLM 5.3 Flash (frontier primary) requires HF_TOKEN,
        // which is present, so frontier resolves to it (not the fallback floor).
        Assert.Equal("hf-glm-5.3-flash", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);
        Assert.Equal("hf-qwen3-coder-next", aliases["fallback"]);

        // No absent-key model appears as a primary.
        Assert.DoesNotContain("kimi-k2", aliases.Values);
        Assert.DoesNotContain("deepseek-v4-pro", aliases.Values);
        Assert.DoesNotContain("deepseek-v4-flash", aliases.Values);
        Assert.DoesNotContain("deepseek-v4-flash-vision-exp", aliases.Values);

        Assert.Contains("HF_TOKEN", summary, StringComparison.Ordinal);
    }

    // ── 2. HF + DeepSeek ─────────────────────────────────────────────────
    [Fact]
    public void HfPlusDeepSeek_CheapFlashVision_BalancedPro_FrontierPro()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("deepseek-v4-flash-vision-exp", aliases["cheap"]);
        Assert.Equal("deepseek-v4-pro", aliases["balanced"]);
        // The frontier primary needs HF_TOKEN (present), so it wins ahead
        // of the deepseek-v4-pro fallback even when DEEPSEEK_API_KEY is set.
        Assert.Equal("hf-glm-5.3-flash", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);
        Assert.Equal("hf-qwen3-coder-next", aliases["fallback"]);
        Assert.DoesNotContain("kimi-k2", aliases.Values);

        foreach (var tier in new[] { "cheap", "balanced", "frontier" })
            Assert.True(BackendConfigGeneratorTestHelpers.ChainTerminatesInFallback(tier, fallbacks),
                $"fallback chain for {tier} should terminate in fallback");

        // Vision must not fall back to a text model.
        Assert.True(fallbacks.ContainsKey("vision"));
        Assert.DoesNotContain("fallback", fallbacks["vision"]);
        Assert.DoesNotContain("kimi-k2", fallbacks["vision"]);
    }

    // ── 3. Trio: HF + DeepSeek + Moonshot ────────────────────────────────
    [Fact]
    public void Trio_FrontierKimi_ChainTerminatesInFallback()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY" };
        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        Assert.Equal("deepseek-v4-flash-vision-exp", aliases["cheap"]);
        Assert.Equal("deepseek-v4-pro", aliases["balanced"]);
        // The HF route to GLM 5.3 Flash is the frontier primary; kimi-k2 drops
        // to the first fallback.
        Assert.Equal("hf-glm-5.3-flash", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);

        Assert.True(fallbacks.ContainsKey("frontier"));
        var chain = fallbacks["frontier"];
        Assert.Contains("kimi-k2", chain);
        Assert.Contains("deepseek-v4-pro", chain);
        Assert.Contains("hf-qwen3-coder-next", chain);
        Assert.Equal(BackendConfigGenerator.FallbackFloorModel, chain[^1]);

        foreach (var tier in new[] { "cheap", "balanced", "frontier" })
            Assert.True(BackendConfigGeneratorTestHelpers.ChainTerminatesInFallback(tier, fallbacks));

        // Vision must not fall back to a text model.
        Assert.True(fallbacks.ContainsKey("vision"));
        Assert.DoesNotContain("fallback", fallbacks["vision"]);
        Assert.DoesNotContain("kimi-k2", fallbacks["vision"]);
    }

    // ── 5. Shape guard ───────────────────────────────────────────────────
    [Fact]
    public void ShapeGuard_ParsesAndEveryTierHasNonEmptyChainEndingInFallback()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var aliases = BackendConfigGeneratorTestHelpers.GeneratedAliases(present);
        var fallbacks = BackendConfigGeneratorTestHelpers.GeneratedFallbacks(present);

        foreach (var tier in new[] { "cheap", "balanced", "frontier", "fallback" })
        {
            Assert.True(aliases.ContainsKey(tier), $"tier '{tier}' must have an alias");
            Assert.False(string.IsNullOrWhiteSpace(aliases[tier]),
                $"alias for '{tier}' must be non-empty");
        }

        // Every tier bottoms out at the floor model. The fallback tier IS the
        // floor, so it has no chain of its own to terminate — the proxy's
        // config gave it a self-referential entry; the resolved chain does not.
        foreach (var tier in new[] { "cheap", "balanced", "frontier" })
            Assert.True(BackendConfigGeneratorTestHelpers.ChainTerminatesInFallback(tier, fallbacks),
                $"fallback chain for {tier} should terminate in the floor model");

        Assert.Equal(BackendConfigGenerator.FallbackFloorModel, aliases["fallback"]);
        Assert.False(fallbacks.ContainsKey("fallback"),
            "the floor tier has nothing to fall back to");

        // Vision tier: present with a vision-only fallback chain.
        Assert.True(aliases.ContainsKey("vision"));
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);
        Assert.True(fallbacks.ContainsKey("vision"));
        Assert.DoesNotContain("fallback", fallbacks["vision"]);
        Assert.DoesNotContain("kimi-k2", fallbacks["vision"]);
    }

    // ── 6. Summary line ──────────────────────────────────────────────────
    [Fact]
    public void Summary_MentionsDetectedKeysAndResolution()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var summary = BackendConfigGenerator.Summarize(present);

        Assert.Contains("HF_TOKEN", summary, StringComparison.Ordinal);
        Assert.Contains("DEEPSEEK_API_KEY", summary, StringComparison.Ordinal);
        Assert.Contains("cheap", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frontier", summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── 7. Degenerate: no keys at all ────────────────────────────────────
    [Fact]
    public void EmptyKeySet_ThrowsInvalidOperationException()
    {
        // Summarize refuses a degenerate answer when no key is present.
        // Callers detect this up front and say so, rather than rendering a tier
        // table of models they cannot reach.
        var present = new HashSet<string>();
        var ex = Assert.Throws<InvalidOperationException>(
            () => BackendConfigGenerator.Summarize(present));
        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider keys", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 8. Tier rows ─────────────────────────────────────────────────────
    [Fact]
    public void TierRows_HfOnlyAndDeepSeek()
    {
        var hf = new HashSet<string> { "HF_TOKEN" };
        var hfRows = BackendConfigGenerator.GetTierRows(hf);
        Assert.Equal(5, hfRows.Count);
        var cheap = hfRows.First(r => r.Tier == "cheap");
        Assert.Equal("fallback", cheap.Model);
        Assert.Equal("Hugging Face", cheap.ProviderName);
        Assert.True(cheap.KeyPresent);

        var ds = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var dsRows = BackendConfigGenerator.GetTierRows(ds);
        var cheapDs = dsRows.First(r => r.Tier == "cheap");
        Assert.Equal("deepseek-v4-flash-vision-exp", cheapDs.Model);
        Assert.Equal("DeepSeek", cheapDs.ProviderName);
        Assert.True(cheapDs.KeyPresent);
        var balanced = dsRows.First(r => r.Tier == "balanced");
        Assert.Equal("deepseek-v4-pro", balanced.Model);
        Assert.Equal("DeepSeek", balanced.ProviderName);
    }

    /// <summary>
    /// Every tier that resolves carries a fallback chain, and with no keys at
    /// all every remaining row reports its key as absent. The vision tier is
    /// omitted entirely rather than degrading, so it is absent from both sets.
    /// </summary>
    [Fact]
    public void TierRows_AllKeysAndEmptyKeys()
    {
        var all = new HashSet<string>
        {
            "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY", "ZAI_API_KEY",
        };
        var allRows = BackendConfigGenerator.GetTierRows(all);
        foreach (var row in allRows.Where(r => r.Tier != "vision"))
            Assert.NotNull(row.FallbackChainText);

        var empty = new HashSet<string>();
        var emptyRows = BackendConfigGenerator.GetTierRows(empty);
        Assert.NotEmpty(emptyRows);
        Assert.DoesNotContain(emptyRows, r => r.Tier == "vision");
        foreach (var row in emptyRows)
            Assert.False(row.KeyPresent);
    }

}
