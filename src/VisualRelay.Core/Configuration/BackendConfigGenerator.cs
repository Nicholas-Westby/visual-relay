namespace VisualRelay.Core.Configuration;

/// <summary>
/// Generates a LiteLLM proxy config YAML by rewriting only the
/// <c>router_settings.model_group_alias</c> and <c>router_settings.fallbacks</c>
/// blocks based on which provider keys are present. <c>model_list</c> and
/// <c>litellm_settings</c> are preserved verbatim from the template.
/// </summary>
public static class BackendConfigGenerator
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
    /// stages — that day, swival's placeholder injection (alias containing
    /// "deepseek") is the known mitigation: rename these aliases back to
    /// include a "deepseek" substring so swival's _needs_reasoning_content
    /// allowlist injects the placeholder into resent assistant tool-call
    /// history. Until then the clean names are fine empirically (swival 1.0.30
    /// on generic-provider + localhost LiteLLM, 33 proxied calls, zero 4xx).
    /// </summary>
    internal static readonly Dictionary<string, List<(string Model, string RequiredKey)>> Chains = new()
    {
        ["cheap"] =
        [
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
            ("glm-5.2", "HF_TOKEN"),
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("hf-qwen3-coder-next", "HF_TOKEN"),
            ("fallback", "HF_TOKEN"),
        ],
        ["vision"] =
        [
            ("hf-qwen3-vl-235b", "HF_TOKEN"),
            ("hf-qwen3-vl-30b", "HF_TOKEN"),
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("fallback", "HF_TOKEN"),
        ],
        ["claude"] =
        [
            ("claude-opus-1m", "ANTHROPIC_API_KEY"),
            ("claude-sonnet", "ANTHROPIC_API_KEY"),
        ],
        ["fallback"] =
        [
            ("hf-qwen3-coder-next", "HF_TOKEN"),
        ],
    };

    /// <summary>Env-var → human-readable provider name.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProviderNames = new Dictionary<string, string>
    {
        ["HF_TOKEN"] = "Hugging Face",
        ["DEEPSEEK_API_KEY"] = "DeepSeek",
        ["MOONSHOT_API_KEY"] = "Moonshot",
        ["ANTHROPIC_API_KEY"] = "Anthropic",
        ["OPENAI_API_KEY"] = "OpenAI",
    };

    /// <summary>Model name → required env var (excluding "fallback" alias).</summary>
    private static readonly IReadOnlyDictionary<string, string> ModelToKey = Chains.Values
        .SelectMany(c => c)
        .Where(c => c.Model != "fallback")
        .DistinctBy(c => c.Model)
        .ToDictionary(c => c.Model, c => c.RequiredKey);

    /// <summary>Structured per-tier row for UI rendering.</summary>
    public sealed record TierConfigRow(
        string Tier,
        string Model,
        string ProviderName,
        string RequiredKey,
        bool KeyPresent,
        string? FallbackChainText);

    /// <summary>
    /// Returns one row per tier with the resolved model, provider, and
    /// key-present status for UI display.
    /// </summary>
    public static IReadOnlyList<TierConfigRow> GetTierRows(ISet<string> presentKeys)
    {
        var (aliases, fallbacks) = ResolveTiers(presentKeys);
        var rows = new List<TierConfigRow>();

        foreach (var tier in Chains.Keys)
        {
            if (tier == "claude" && !aliases.ContainsKey("claude"))
            {
                rows.Add(new TierConfigRow(
                    Tier: "claude",
                    Model: "(key missing)",
                    ProviderName: "Anthropic",
                    RequiredKey: "ANTHROPIC_API_KEY",
                    KeyPresent: false,
                    FallbackChainText: null));
                continue;
            }

            if (!aliases.TryGetValue(tier, out var model))
                continue;

            var requiredKey = model == "fallback" ? "HF_TOKEN" : ModelToKey[model];
            var chainText = fallbacks.TryGetValue(tier, out var fb) && fb.Count > 0
                ? string.Join(", ", fb)
                : null;

            rows.Add(new TierConfigRow(
                Tier: tier,
                Model: model,
                ProviderName: ProviderNames[requiredKey],
                RequiredKey: requiredKey,
                KeyPresent: presentKeys.Contains(requiredKey),
                FallbackChainText: chainText));
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
    /// <returns>
    /// A tuple of the generated YAML text and a one-line human-readable summary
    /// of tier→model resolutions and detected keys.
    /// </returns>
    public static (string Yaml, string Summary) Generate(ISet<string> presentKeys, string templatePath)
    {
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

        var (aliases, fallbacks) = ResolveTiers(presentKeys);

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
        if (!aliases.ContainsKey("claude"))
            tierResolutions.Add("claude→(absent)");

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
        ResolveTiers(ISet<string> presentKeys)
    {
        var aliases = new Dictionary<string, string>();
        var fallbacks = new Dictionary<string, List<string>>();

        foreach (var (tier, candidates) in Chains)
        {
            // Claude is opt-in premium: omit entirely when key is absent.
            if (tier == "claude" && !presentKeys.Contains("ANTHROPIC_API_KEY"))
                continue;

            var survivors = candidates
                .Where(c => presentKeys.Contains(c.RequiredKey))
                .Select(c => c.Model)
                .ToList();

            // Degenerate: no key at all. Still produce a valid alias so the
            // proxy boots (the model defs exist, just no api_key value).
            if (survivors.Count == 0)
            {
                if (tier == "claude") continue;
                aliases[tier] = tier == FallbackTier ? FallbackFloorModel : FallbackTier;
                fallbacks[tier] = [FallbackTier];
                continue;
            }

            // When the first surviving model for a non-fallback tier is the
            // HF floor model itself, point the alias at the "fallback" tier
            // directly so the floor is always reached through one indirection.
            string alias;
            int chainStart;
            if (tier != FallbackTier && survivors[0] == FallbackFloorModel)
            {
                alias = FallbackTier;
                chainStart = 1; // hf-qwen3-coder-next is reachable via fallback
            }
            else
            {
                alias = survivors[0];
                chainStart = 1;
            }

            aliases[tier] = alias;

            var chain = survivors.Skip(chainStart).ToList();

            // Every non-claude chain must terminate in the fallback tier.
            if (tier != "claude" && (chain.Count == 0 || chain[^1] != FallbackTier))
                chain.Add(FallbackTier);

            if (chain.Count > 0)
                fallbacks[tier] = chain;
        }

        return (aliases, fallbacks);
    }
}
