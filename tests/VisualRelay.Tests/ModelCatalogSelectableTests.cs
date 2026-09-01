using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class ModelCatalogSelectableTests
{
    // ── Override-aware resolution ────────────────────────────────────────

    /// <summary>
    /// When an override is provided for a tier AND the override model's
    /// required key is present, the override wins as the alias and the
    /// fallback chain comprises the remaining survivors (still terminating
    /// in <c>fallback</c> for every tier that is allowed to degrade).
    /// <c>kimi-k2</c> is the subject because it sits outside the cheap chain
    /// entirely, so the override cannot be confused with auto-resolution.
    /// </summary>
    [Fact]
    public void Override_WinsWhenKeyPresent()
    {
        var present = new HashSet<string> { "HF_TOKEN", "MOONSHOT_API_KEY" };
        var overrides = new Dictionary<string, string> { ["cheap"] = "kimi-k2" };

        var aliases = ModelCatalogTestHelpers.GeneratedAliases(present, overrides);
        var fallbacks = ModelCatalogTestHelpers.GeneratedFallbacks(present, overrides);

        // cheap should use the override model kimi-k2, not its auto-resolved model.
        Assert.Equal("kimi-k2", aliases["cheap"]);

        // The fallback chain for cheap must still terminate in fallback.
        Assert.True(ModelCatalogTestHelpers.ChainTerminatesInFallback("cheap", fallbacks));

        // Other tiers unaffected by the override resolve normally.
        Assert.Equal("hf-glm-5.3-flash", aliases["frontier"]);
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
        // Only HF_TOKEN is present; MOONSHOT_API_KEY is absent so the
        // kimi-k2 override must be ignored.
        var present = new HashSet<string> { "HF_TOKEN" };
        var overrides = new Dictionary<string, string> { ["cheap"] = "kimi-k2" };

        var aliases = ModelCatalogTestHelpers.GeneratedAliases(present, overrides);

        // cheap should fall through to the HF floor ("fallback"), not kimi-k2.
        Assert.Equal(ModelCatalog.FallbackFloorModel, aliases["cheap"]);
        Assert.DoesNotContain("kimi-k2", aliases.Values);
    }

    // ── SelectableModels shape ───────────────────────────────────────────

    [Fact]
    public void SelectableModels_PerTierShapeAndCapped()
    {
        var sm = ModelCatalog.SelectableModelsByTier;

        // Every tier from Chains must be represented.
        foreach (var tier in ModelCatalog.Chains.Keys)
            Assert.True(sm.ContainsKey(tier), $"SelectableModels missing tier '{tier}'");

        // Each list ≤ 6 entries.
        foreach (var (tier, models) in sm)
            Assert.True(models.Count <= 6, $"Tier '{tier}' has {models.Count} selectable models (max 6)");

        // All model names must be models the catalog can actually route to.
        var realSet = ModelCatalogTestHelpers.RoutableModels();

        foreach (var (tier, models) in sm)
            foreach (var model in models)
                Assert.True(realSet.Contains(model),
                    $"SelectableModels tier '{tier}' has unknown model '{model}'");

        // Every real model appears in at least one tier's selectable list.
        var allSelectable = sm.Values.SelectMany(m => m).ToHashSet();
        foreach (var model in realSet)
            Assert.True(allSelectable.Contains(model),
                $"Real model '{model}' is missing from all SelectableModels");
    }

    // ── GetTierRows exposes IsEditable and SelectableModels ──────────────

    [Fact]
    public void GetTierRows_ExposesIsEditableAndSelectableModels()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var rows = ModelCatalog.GetTierRows(present);

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
}
