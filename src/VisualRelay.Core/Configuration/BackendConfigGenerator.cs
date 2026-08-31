namespace VisualRelay.Core.Configuration;

/// <summary>
/// Generates a LiteLLM proxy config YAML by rewriting only the
/// <c>router_settings.model_group_alias</c> and <c>router_settings.fallbacks</c>
/// blocks based on which provider keys are present. <c>model_list</c> and
/// <c>litellm_settings</c> are preserved verbatim from the template.
/// </summary>
public static partial class BackendConfigGenerator
{
    /// <summary>Fallback-floor model the <c>fallback</c> tier alias resolves to.</summary>
    private const string FallbackFloorModel = "hf-qwen3-coder-next";

    /// <summary>Tier alias name for the always-available HF floor.</summary>
    private const string FallbackTier = "fallback";

    /// <summary>
    /// Ordered candidate list per tier. Each entry is a
    /// (<c>model_name</c>, <c>required_env_var</c>) pair. The string
    /// <c>"fallback"</c> as a model name represents the fallback tier alias
    /// (which itself resolves to <see cref="FallbackFloorModel"/>).
    /// WATCH: if DeepSeek ever starts ENFORCING reasoning_content on tool-call
    /// history, the failure signature is HTTP 400 from turn 2 of tool-calling
    /// stages. RENAMING THESE ALIASES WOULD NOT HELP — that remedy was recorded
    /// in error and is corrected here on 2026-08-31: swival 1.0.38's
    /// _needs_reasoning_content keys on BASE URL, never the model name, and says
    /// so in its own docstring. The relay always points swival at 127.0.0.1:4000,
    /// so the gate is false whatever an alias is called, and litellm's
    /// DeepSeekChatConfig._fill_reasoning_content is inert too — it fires only
    /// once the caller sends thinking/reasoning_effort, which nothing here does.
    /// Both nets are off; the stack passes empirically (33 proxied calls, zero
    /// 4xx) only because DeepSeek does not enforce the rule today. Real levers
    /// that day: pass swival's --reasoning-effort, or replay it via --llm-filter.
    /// </summary>
    internal static readonly Dictionary<string, List<(string Model, string RequiredKey)>> Chains = new()
    {
        // The vision-exp head and the text-only -flash behind it are the same
        // size at the same rates, so the second DeepSeek hop costs nothing and
        // covers the one risk the experimental model carries: withdrawal without
        // notice. Provider diversification resumes at the fallback tier.
        ["cheap"] =
        [
            ("deepseek-v4-flash-vision-exp", "DEEPSEEK_API_KEY"),
            ("deepseek-v4-flash", "DEEPSEEK_API_KEY"),
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("fallback", "HF_TOKEN"),
        ],
        ["balanced"] =
        [
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("deepseek-v4-flash", "DEEPSEEK_API_KEY"),
            ("fallback", "HF_TOKEN"),
        ],
        ["frontier"] =
        [
            ("glm-5.3-flash", "ZAI_API_KEY"),
            ("hf-glm-5.3-flash", "HF_TOKEN"),
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("hf-qwen3-coder-next", "HF_TOKEN"),
            ("fallback", "HF_TOKEN"),
        ],
        ["vision"] =
        [
            ("hf-qwen3-vl-235b", "HF_TOKEN"),
            ("hf-qwen3-vl-30b", "HF_TOKEN"),
        ],
        ["fallback"] =
        [
            ("hf-qwen3-coder-next", "HF_TOKEN"),
        ],
    };

    /// <summary>
    /// True for a tier that is omitted entirely when no present key backs it,
    /// rather than degrading to the fallback chain. Only <c>vision</c> qualifies:
    /// an image sent to a text model is answered confidently and wrongly, so a
    /// hard "model not found" is the safer failure. Every other tier degrades.
    /// </summary>
    private static bool OmittedWhenUnbacked(string tier) => tier == "vision";

    /// <summary>Model name → required env var (excluding "fallback" alias).</summary>
    private static readonly IReadOnlyDictionary<string, string> ModelToKey = Chains.Values
        .SelectMany(c => c)
        .Where(c => c.Model != "fallback")
        .DistinctBy(c => c.Model)
        .ToDictionary(c => c.Model, c => c.RequiredKey);

    /// <summary>Structured per-tier row for UI rendering.</summary>
    public sealed partial record TierConfigRow(
        string Tier,
        string Model,
        string ProviderName,
        bool KeyPresent,
        string? FallbackChainText);

    /// <summary>
    /// Returns one row per tier with the resolved model, provider, and
    /// key-present status for UI display.
    /// </summary>
    public static IReadOnlyList<TierConfigRow> GetTierRows(
        ISet<string> presentKeys,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var (aliases, fallbacks) = ResolveTiers(presentKeys, overrides);
        var rows = new List<TierConfigRow>();

        foreach (var tier in Chains.Keys)
        {
            if (!aliases.TryGetValue(tier, out var model))
                continue;

            var requiredKey = model == "fallback" ? "HF_TOKEN" : GetRequiredKey(model);
            var chainText = fallbacks.TryGetValue(tier, out var fb) && fb.Count > 0
                ? string.Join(", ", fb)
                : null;

            rows.Add(new TierConfigRow(
                Tier: tier,
                Model: model,
                ProviderName: ProviderNames[requiredKey],
                KeyPresent: presentKeys.Contains(requiredKey),
                FallbackChainText: chainText)
            {
                SelectableModels = SelectableModelsByTier.TryGetValue(tier, out var slm) ? slm : [],
                IsEditable = tier != FallbackTier,
            });
        }

        return rows;
    }

    /// <summary>
    /// Generates a LiteLLM config YAML from <paramref name="templatePath"/>,
    /// rewriting aliases and fallbacks so every tier points at the best model
    /// whose required key is in <paramref name="presentKeys"/>.
    /// </summary>
    /// <param name="presentKeys">Set of environment variable names that are set.</param>
    /// <param name="templatePath">Path to the static <c>litellm-config.yaml</c> template.</param>
    /// <param name="overrides">Optional per-tier model overrides applied when the chosen model's key is present.</param>
    /// <returns>
    /// A tuple of the generated YAML text and a one-line human-readable summary
    /// of tier→model resolutions and detected keys.
    /// </returns>
    public static (string Yaml, string Summary) Generate(
        ISet<string> presentKeys,
        string templatePath,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (presentKeys.Count == 0)
            throw new InvalidOperationException(
                "BackendConfigGenerator.Generate called with zero provider keys. " +
                "Callers must detect this condition and fall back to the static template.");

        var lines = File.ReadAllLines(templatePath);

        // Locate the boundary markers in the template.
        var aliasStart = -1;
        var litellmStart = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "  model_group_alias:") aliasStart = i;
            if (lines[i] == "litellm_settings:") litellmStart = i;
        }

        if (aliasStart < 0 || litellmStart < 0)
            throw new InvalidOperationException(
                "Template is missing required sections (model_group_alias / litellm_settings).");

        var (aliases, fallbacks) = ResolveTiers(presentKeys, overrides);

        // Reassemble the YAML: verbatim prefix + generated aliases/fallbacks + verbatim suffix.
        var result = new List<string>(lines.Length);

        // Everything before model_group_alias (model_list, stream_timeout).
        for (var i = 0; i < aliasStart; i++)
            result.Add(lines[i]);

        // Generated model_group_alias block.
        result.Add("  model_group_alias:");
        foreach (var (tier, model) in aliases.OrderBy(a => a.Key, StringComparer.Ordinal))
            result.Add($"    {tier}: {model}");
        result.Add("");

        // Generated fallbacks block.
        result.Add("  fallbacks:");
        foreach (var (tier, chain) in fallbacks.OrderBy(f => f.Key, StringComparer.Ordinal))
            result.Add($"    - {tier}: [{string.Join(", ", chain)}]");
        result.Add("");

        // Everything from litellm_settings onward.
        for (var i = litellmStart; i < lines.Length; i++)
            result.Add(lines[i]);

        var yaml = string.Join("\n", result) + "\n";

        // One-line summary for stderr / logs.
        var tierResolutions = new List<string>();
        foreach (var (tier, model) in aliases.OrderBy(a => a.Key, StringComparer.Ordinal))
            tierResolutions.Add($"{tier}→{model}");

        var keysDetected = presentKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var summary = keysDetected.Count > 0
            ? $"backend: config generated — {string.Join(", ", tierResolutions)}; keys: {string.Join(", ", keysDetected)}"
            : $"backend: config generated — {string.Join(", ", tierResolutions)}; keys: (none)";

        return (yaml, summary);
    }

    /// <summary>
    /// Resolves aliases and fallbacks from the candidate chains for the
    /// given set of present keys.
    /// </summary>
    private static (Dictionary<string, string> Aliases, Dictionary<string, List<string>> Fallbacks)
        ResolveTiers(ISet<string> presentKeys, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var aliases = new Dictionary<string, string>();
        var fallbacks = new Dictionary<string, List<string>>();

        foreach (var (tier, candidates) in Chains)
        {
            // --- Override path ---
            if (overrides is not null && overrides.TryGetValue(tier, out var ov))
            {
                if (TryApplyOverride(tier, ov, presentKeys, candidates, aliases, fallbacks))
                    continue;
                // Key absent → ignore override, fall through to auto-resolve.
            }

            var chain = candidates
                .Where(c => presentKeys.Contains(c.RequiredKey))
                .Select(c => c.Model)
                .ToList();

            // Degenerate: no key at all. An unbacked vision tier is skipped
            // entirely so a request produces "model not found" instead of
            // a silent fallback to a text model. Other tiers still produce
            // a valid alias so the proxy boots (the model defs exist, just
            // no api_key value).
            if (chain.Count == 0)
            {
                if (OmittedWhenUnbacked(tier)) continue;
                aliases[tier] = tier == FallbackTier ? FallbackFloorModel : FallbackTier;
                fallbacks[tier] = [FallbackTier];
                continue;
            }

            // When the first surviving model for a non-fallback tier is the
            // HF floor model itself, point the alias at the "fallback" tier
            // directly so the floor is always reached through one indirection.
            string alias;
            int chainStart;
            if (tier != FallbackTier && chain[0] == FallbackFloorModel)
            {
                alias = FallbackTier;
                chainStart = 1; // hf-qwen3-coder-next is reachable via fallback
            }
            else
            {
                alias = chain[0];
                chainStart = 1;
            }

            aliases[tier] = alias;

            var fb = chain.Skip(chainStart).ToList();

            // Every chain that is allowed to degrade must terminate in the fallback tier.
            if (!OmittedWhenUnbacked(tier) && (fb.Count == 0 || fb[^1] != FallbackTier))
                fb.Add(FallbackTier);

            if (fb.Count > 0)
                fallbacks[tier] = fb;
        }

        return (aliases, fallbacks);
    }

}
