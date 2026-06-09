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
    /// </summary>
    private static readonly Dictionary<string, List<(string Model, string RequiredKey)>> Chains = new()
    {
        ["cheap-kimi"] = new()
        {
            ("deepseek-v4-flash", "DEEPSEEK_API_KEY"),
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("fallback", "HF_TOKEN"),
        },
        ["balanced-kimi"] = new()
        {
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("deepseek-v4-flash", "DEEPSEEK_API_KEY"),
            ("fallback", "HF_TOKEN"),
        },
        ["frontier"] = new()
        {
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("hf-qwen3-coder-next", "HF_TOKEN"),
            ("fallback", "HF_TOKEN"),
        },
        ["vision"] = new()
        {
            ("hf-qwen3-vl-235b", "HF_TOKEN"),
            ("hf-qwen3-vl-30b", "HF_TOKEN"),
            ("kimi-k2", "MOONSHOT_API_KEY"),
            ("fallback", "HF_TOKEN"),
        },
        ["claude"] = new()
        {
            ("claude-opus-1m", "ANTHROPIC_API_KEY"),
            ("claude-sonnet", "ANTHROPIC_API_KEY"),
        },
        ["fallback"] = new()
        {
            ("hf-qwen3-coder-next", "HF_TOKEN"),
        },
    };

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

        // Locate the three boundary markers in the template.
        var aliasStart = -1;
        var fallbackStart = -1;
        var litellmStart = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "  model_group_alias:") aliasStart = i;
            if (lines[i] == "  fallbacks:") fallbackStart = i;
            if (lines[i] == "litellm_settings:") litellmStart = i;
        }

        if (aliasStart < 0 || litellmStart < 0)
            throw new InvalidOperationException(
                "Template is missing required sections (model_group_alias / litellm_settings).");

        // Resolve aliases and fallbacks from the candidate chains.
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
                fallbacks[tier] = new List<string> { FallbackTier };
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
}
