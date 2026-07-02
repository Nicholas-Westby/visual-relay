namespace VisualRelay.Core.Configuration;

public static partial class BackendConfigGenerator
{
    /// <summary>
    /// Curated per-tier lists of selectable models (≤6 each). Only real
    /// <c>model_list</c> models from the five in-use providers. Defaults
    /// match today's auto-resolution.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SelectableModelsByTier =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["frontier"] = new List<string>
            {
                "glm-5.2", "kimi-k2", "deepseek-v4-pro",
                "claude-opus-1m", "gpt-5", "hf-qwen3-coder-next",
            },
            ["balanced"] = new List<string>
            {
                "deepseek-v4-pro", "kimi-k2", "deepseek-v4-flash",
                "gpt-5", "hf-qwen3-coder-next", "claude-sonnet",
            },
            ["cheap"] = new List<string>
            {
                "deepseek-v4-flash", "deepseek-v4-pro",
                "hf-qwen3-coder-next", "gpt-5",
            },
            ["vision"] = new List<string>
            {
                "hf-qwen3-vl-235b", "hf-qwen3-vl-30b", "kimi-k2",
            },
            ["claude"] = new List<string>
            {
                "claude-opus-1m", "claude-sonnet",
            },
            ["fallback"] = new List<string>
            {
                "hf-qwen3-coder-next",
            },
        };

    /// <summary>
    /// Model name → required env var for models that appear only in
    /// <see cref="SelectableModelsByTier"/> and not in <see cref="Chains"/>
    /// (e.g. <c>gpt-5</c>). Merged with <see cref="ModelToKey"/> at
    /// resolution time so overrides referencing these models can still
    /// resolve their provider key.
    /// </summary>
    private static readonly Dictionary<string, string> ModelToRequiredKey = new()
    {
        ["gpt-5"] = "OPENAI_API_KEY",
    };

    /// <summary>Selectable model names for the tier and whether it is user-editable.</summary>
    public partial record TierConfigRow
    {
        public IReadOnlyList<string> SelectableModels { get; init; } = [];
        public bool IsEditable { get; init; } = true;
    }

    /// <summary>Resolves the required env-var key for a model name,
    /// merging <see cref="ModelToKey"/> and <see cref="ModelToRequiredKey"/>.</summary>
    internal static string GetRequiredKey(string model)
    {
        if (model == "fallback") return "HF_TOKEN";
        if (ModelToKey.TryGetValue(model, out var key)) return key;
        // defensive: unknown models default to HF floor
        return ModelToRequiredKey.GetValueOrDefault(model, "HF_TOKEN");
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
            if (tier != "claude" && (survivors.Count == 0 || survivors[^1] != FallbackTier))
                survivors.Add(FallbackTier);
            if (survivors.Count > 0)
                fallbacks[tier] = survivors;
        }
        else if (tier != "claude")
        {
            fallbacks[tier] = [FallbackTier];
        }

        return true;
    }
}
