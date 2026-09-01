namespace VisualRelay.Core.Configuration;

public static partial class ModelCatalog
{
    /// <summary>
    /// Curated per-tier lists of selectable models (≤6 each). Only models that
    /// are actually routable on the four in-use providers. Defaults match
    /// today's auto-resolution.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SelectableModelsByTier =
        new Dictionary<string, IReadOnlyList<string>>
        {
            // hf-qwen3-coder-next is deliberately absent: the ≤6 cap has no
            // room for it once both GLM 5.3 Flash routes head the list, it is
            // the weakest option a frontier tier could pick, and the
            // auto-resolved frontier chain still reaches it (and the fallback
            // tier) anyway.
            ["frontier"] = new List<string>
            {
                "glm-5.3-flash", "hf-glm-5.3-flash", "kimi-k2",
                "deepseek-v4-pro",
            },
            ["balanced"] = new List<string>
            {
                "deepseek-v4-pro", "kimi-k2", "deepseek-v4-flash",
                "hf-qwen3-coder-next",
            },
            // deepseek-v4-flash keeps its slot behind the vision-exp default: it
            // is the auto-resolved first fallback, and dropping it from the
            // picker would silently discard a saved override that names it.
            ["cheap"] = new List<string>
            {
                "deepseek-v4-flash-vision-exp", "deepseek-v4-flash",
                "deepseek-v4-pro", "hf-qwen3-coder-next",
            },
            ["vision"] = new List<string>
            {
                "hf-qwen3-vl-235b", "hf-qwen3-vl-30b",
            },
            ["fallback"] = new List<string>
            {
                "hf-qwen3-coder-next",
            },
        };

    /// <summary>Selectable model names for the tier and whether it is user-editable.</summary>
    public partial record TierRow
    {
        public IReadOnlyList<string> SelectableModels { get; init; } = [];
        public bool IsEditable { get; init; } = true;
    }

    /// <summary>Resolves the required env-var key for a model name.
    /// Every selectable model is now also a <see cref="Chains"/> model, so
    /// <see cref="ModelToKey"/> is the only source.</summary>
    internal static string GetRequiredKey(string model)
    {
        if (model == "fallback") return "HF_TOKEN";
        // defensive: unknown models default to HF floor
        return ModelToKey.GetValueOrDefault(model, "HF_TOKEN");
    }

    /// <summary>Attempts to apply a tier-model override. Returns true when
    /// the override was applied (key present), false when it should be
    /// ignored (key absent) and auto-resolution must proceed.</summary>
    internal static bool TryApplyOverride(
        string tier,
        string ov,
        ISet<string> presentKeys,
        List<(string Model, string RequiredKey)> candidates,
        Dictionary<string, string> aliases,
        Dictionary<string, List<string>> fallbacks)
    {
        var ovKey = GetRequiredKey(ov);
        if (!presentKeys.Contains(ovKey))
            return false;

        // Override wins: alias = ov, fallbacks = chain survivors
        // (excluding ov itself), still terminating in fallback.
        var survivors = candidates
            .Where(c => presentKeys.Contains(c.RequiredKey))
            .Select(c => c.Model)
            .Where(m => m != ov)
            .ToList();

        aliases[tier] = ov;

        if (survivors.Count > 0)
        {
            if (tier != FallbackTier && survivors[0] == FallbackFloorModel)
                survivors.RemoveAt(0);
            if (!OmittedWhenUnbacked(tier) && (survivors.Count == 0 || survivors[^1] != FallbackTier))
                survivors.Add(FallbackTier);
            if (survivors.Count > 0)
                fallbacks[tier] = survivors;
        }
        else if (!OmittedWhenUnbacked(tier))
        {
            fallbacks[tier] = [FallbackTier];
        }

        return true;
    }
}
