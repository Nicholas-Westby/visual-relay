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
    /// <summary>The always-available model every chain terminates in.</summary>
    public const string FallbackFloorModel = "hf-qwen3-coder-next";

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
    /// in error and is corrected here on 2026-08-31: the two compatibility nets
    /// that used to replay reasoning_content both keyed on the base URL or on an
    /// explicit thinking/reasoning_effort field, never on the alias, and both went
    /// with the retired proxy stack. Nothing on the direct path sends the field at
    /// all. The stack passes empirically (33 calls, zero 4xx) only because DeepSeek
    /// does not enforce the rule today. The lever that day is to send
    /// reasoning_effort on the request itself.
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
            // A tier with no backing key does not RESOLVE, but it is still
            // shown: the panel's job is to tell the user which key would make
            // it work, and hiding it says nothing. The first candidate is what
            // the tier would use, and KeyPresent is false.
            //
            // Vision is the exception, and the same named rule governs it here
            // as in routing: an unbacked vision tier is omitted entirely rather
            // than offered, so nothing suggests an image request would work.
            var resolved = aliases.TryGetValue(tier, out var alias);
            if (!resolved && OmittedWhenUnbacked(tier)) continue;

            var model = resolved ? alias! : Chains[tier][0].Model;

            var requiredKey = model == "fallback" ? "HF_TOKEN" : GetRequiredKey(model);
            var chainText = fallbacks.TryGetValue(tier, out var fb) && fb.Count > 0
                ? string.Join(", ", fb)
                : null;

            rows.Add(new TierConfigRow(
                Tier: tier,
                Model: model,
                ProviderName: ProviderNames[requiredKey],
                KeyPresent: resolved && presentKeys.Contains(requiredKey),
                FallbackChainText: chainText)
            {
                SelectableModels = SelectableModelsByTier.TryGetValue(tier, out var slm) ? slm : [],
                IsEditable = tier != FallbackTier,
            });
        }

        return rows;
    }

    /// <summary>
    /// A one-line, human-readable account of how each tier resolves for a given
    /// set of present keys, and which keys those are.
    /// </summary>
    /// <param name="presentKeys">Which provider keys are set.</param>
    /// <param name="overrides">Per-tier model overrides from config.</param>
    /// <returns>The summary line.</returns>
    /// <exception cref="InvalidOperationException">
    /// When no provider key is present. There is nothing to summarize, and a
    /// caller that has not checked would otherwise render a tier table of
    /// models it cannot reach.
    /// </exception>
    /// <remarks>
    /// This replaces a function that rendered a whole LiteLLM YAML document and
    /// returned this line alongside it. The proxy is gone; the line is what
    /// anything actually wanted.
    /// </remarks>
    public static string Summarize(
        ISet<string> presentKeys, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (presentKeys.Count == 0)
            throw new InvalidOperationException(
                "BackendConfigGenerator.Summarize called with zero provider keys. "
                + "Callers must detect this condition and say so instead.");

        var (aliases, _) = ResolveTiers(presentKeys, overrides);

        var resolutions = aliases
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}\u2192{pair.Value}");
        var keys = presentKeys.OrderBy(key => key, StringComparer.Ordinal);

        return $"models resolved \u2014 {string.Join(", ", resolutions)}; keys: {string.Join(", ", keys)}";
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

            // No key backs any model in this tier, so the tier is omitted.
            //
            // Every tier except vision used to resolve anyway, to a model whose
            // key was absent, for one stated reason: so the proxy would boot with
            // the model definitions present and no api_key value. The proxy is
            // gone and the claim was never true of the request — a stage routed
            // there failed at the provider. Omitting it means the caller learns
            // which key is missing instead.
            if (chain.Count == 0) continue;

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
