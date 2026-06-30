using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed partial class BackendConfigGeneratorTests
{
    // ── Override-aware resolution ────────────────────────────────────────

    /// <summary>
    /// When an override is provided for a tier AND the override model's
    /// required key is present, the override wins as the alias and the
    /// fallback chain comprises the remaining survivors (still terminating
    /// in <c>fallback</c> for non-<c>claude</c> tiers).
    /// </summary>
    [Fact]
    public void Override_WinsWhenKeyPresent()
    {
        var present = new HashSet<string> { "HF_TOKEN", "OPENAI_API_KEY" };
        var overrides = new Dictionary<string, string> { ["cheap"] = "gpt-5" };

        var aliases = GeneratedAliases(present, overrides);
        var fallbacks = GeneratedFallbacks(present, overrides);

        // cheap should use the override model gpt-5, not deepseek-v4-flash.
        Assert.Equal("gpt-5", aliases["cheap"]);

        // The fallback chain for cheap must still terminate in fallback.
        Assert.True(ChainTerminatesInFallback("cheap", fallbacks));

        // Other tiers unaffected by the override resolve normally.
        Assert.Equal("glm-5.2", aliases["frontier"]);
    }

    /// <summary>
    /// When an override model's required key is absent, the override is
    /// silently ignored and the tier auto-resolves via its normal chain.
    /// Boot must never break because of a stale override referencing an
    /// unavailable provider.
    /// </summary>
    [Fact]
    public void Override_IgnoredWhenKeyAbsent()
    {
        // Only HF_TOKEN is present; OPENAI_API_KEY is absent so gpt-5
        // override must be ignored.
        var present = new HashSet<string> { "HF_TOKEN" };
        var overrides = new Dictionary<string, string> { ["cheap"] = "gpt-5" };

        var aliases = GeneratedAliases(present, overrides);

        // cheap should fall through to the HF floor ("fallback"), not gpt-5.
        Assert.Equal("fallback", aliases["cheap"]);
        Assert.DoesNotContain("gpt-5", aliases.Values);
    }

    // ── SelectableModels shape ───────────────────────────────────────────

    [Fact]
    public void SelectableModels_PerTierShapeAndCapped()
    {
        var sm = BackendConfigGenerator.SelectableModels;

        // Every tier from Chains must be represented.
        foreach (var tier in BackendConfigGenerator.Chains.Keys)
            Assert.True(sm.ContainsKey(tier), $"SelectableModels missing tier '{tier}'");

        // Each list ≤ 6 entries.
        foreach (var (tier, models) in sm)
            Assert.True(models.Count <= 6, $"Tier '{tier}' has {models.Count} selectable models (max 6)");

        // All model names must come from the real model_list.
        string[] realModels =
        [
            "glm-5.2", "kimi-k2", "deepseek-v4-pro", "deepseek-v4-flash",
            "hf-qwen3-coder-next", "hf-qwen3-vl-235b", "hf-qwen3-vl-30b",
            "claude-opus-1m", "claude-sonnet", "gpt-5",
        ];
        var realSet = new HashSet<string>(realModels);

        foreach (var (tier, models) in sm)
            foreach (var model in models)
                Assert.True(realSet.Contains(model),
                    $"SelectableModels tier '{tier}' has unknown model '{model}'");

        // Every real model appears in at least one tier's selectable list.
        var allSelectable = sm.Values.SelectMany(m => m).ToHashSet();
        foreach (var model in realModels)
            Assert.True(allSelectable.Contains(model),
                $"Real model '{model}' is missing from all SelectableModels");
    }

    // ── GetTierRows exposes IsEditable and SelectableModels ──────────────

    [Fact]
    public void GetTierRows_ExposesIsEditableAndSelectableModels()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var rows = BackendConfigGenerator.GetTierRows(present);

        foreach (var row in rows)
        {
            // SelectableModels must be populated.
            Assert.NotNull(row.SelectableModels);
            Assert.NotEmpty(row.SelectableModels);

            if (row.Tier == "fallback")
                Assert.False(row.IsEditable, "fallback tier must not be editable");
            else
                Assert.True(row.IsEditable, $"tier '{row.Tier}' must be editable");
        }
    }

    // ── Helpers (augmented for override threading) ───────────────────────

    private static Dictionary<string, string> GeneratedAliases(
        ISet<string> keys,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath, overrides);
        return ParseAliases(yaml);
    }

    private static Dictionary<string, List<string>> GeneratedFallbacks(
        ISet<string> keys,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath, overrides);
        return ParseFallbacks(yaml);
    }

    private static (string Yaml, string Summary) Generate(
        ISet<string> keys,
        IReadOnlyDictionary<string, string>? overrides = null) =>
        BackendConfigGenerator.Generate(keys, TemplatePath, overrides);
}
