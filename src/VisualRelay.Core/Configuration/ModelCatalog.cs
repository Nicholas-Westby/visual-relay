namespace VisualRelay.Core.Configuration;

/// <summary>
/// The single source of model truth: which models each tier may use, in what
/// order, and which provider key each one needs. Everything that names a model
/// reads it from here — the agent's chain resolution, the cost estimator, the
/// settings panel's tier rows and the config loader's override validation.
/// <para>
/// This is the tier half of the catalog. <see cref="Llm.Routing.ProviderRoutes"/>
/// is the other half: where each of these models actually lives.
/// </para>
/// </summary>
public static partial class ModelCatalog
{
    /// <summary>The always-available model every chain terminates in.</summary>
    public const string FallbackFloorModel = "hf-qwen3-coder-next";

    /// <summary>Tier alias name for the always-available HF floor.</summary>
    private const string FallbackTier = "fallback";

    /// <summary>
    /// Ordered candidate list per tier. Each entry is a
    /// (model alias, required env var) pair. The string <c>"fallback"</c> as a
    /// model name represents the fallback tier alias (which itself resolves to
    /// <see cref="FallbackFloorModel"/>).
    /// WATCH: if DeepSeek ever starts ENFORCING reasoning_content on tool-call
    /// history, the failure signature is HTTP 400 from turn 2 of tool-calling
    /// stages. RENAMING THESE ALIASES WOULD NOT HELP — that remedy was recorded
    /// in error and is corrected here on 2026-08-31: the two compatibility nets
    /// that used to replay reasoning_content both keyed on the base URL or on an
    /// explicit thinking/reasoning_effort field, never on the alias, and both were
    /// retired with the stack that carried them. Nothing on the direct path sends
    /// the field at all. The stack passes empirically (33 calls, zero 4xx) only
    /// because DeepSeek does not enforce the rule today. The lever that day is to
    /// send reasoning_effort on the request itself.
    /// </summary>
    internal static readonly Dictionary<string, List<(string Model, string RequiredKey)>> Chains = new()
    {
        // Both tiers lead with DeepSeek V4.1 Flash, a million-token model at a
        // quarter of the old Flash input rate. The V4 names behind it are on
        // their way to being the same weights: DeepSeek retired the two Flash
        // ids on 2026-09-10 and routes them to V4.1 Flash today, and v4-pro is
        // still served as itself until 2026-09-14, when it joins them. They are
        // kept in their former order anyway, because "temporarily routed" is
        // exactly the notice that a name can be withdrawn. That safety net is
        // not free: each model gets one retry, so a dead name burns TWO attempts
        // before the next hop, and cheap now spends eight attempts on DeepSeek
        // before it reaches another provider, up from six. Real provider
        // diversification still starts at kimi-k2 on balanced and at the
        // fallback tier on cheap.
        ["cheap"] =
        [
            ("deepseek-flash", "DEEPSEEK_API_KEY"),
            ("deepseek-v4-flash-vision-exp", "DEEPSEEK_API_KEY"),
            ("deepseek-v4-flash", "DEEPSEEK_API_KEY"),
            ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
            ("fallback", "HF_TOKEN"),
        ],
        ["balanced"] =
        [
            ("deepseek-flash", "DEEPSEEK_API_KEY"),
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
        // The DeepSeek route is the TAIL, so an install holding HF_TOKEN routes
        // exactly as before and only reaches it when both VL models are gone.
        // It earns its place at the other end: without it, a DeepSeek-only
        // install got no vision tier at all and could not run a visual review,
        // while a vision-capable DeepSeek model sat on its cheap tier. That
        // exclusion was made on 2026-08-31 because the proxy then in front of
        // the agent stripped image parts on the DeepSeek route; the proxy was
        // deleted the next day and images now reach the provider verbatim.
        ["vision"] =
        [
            ("hf-qwen3-vl-235b", "HF_TOKEN"),
            ("hf-qwen3-vl-30b", "HF_TOKEN"),
            ("deepseek-v4-flash-vision-exp", "DEEPSEEK_API_KEY"),
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
    public sealed partial record TierRow(
        string Tier,
        string Model,
        string ProviderName,
        bool KeyPresent,
        string? FallbackChainText);

    /// <summary>
    /// Returns one row per tier with the resolved model, provider, and
    /// key-present status for UI display.
    /// </summary>
    public static IReadOnlyList<TierRow> GetTierRows(
        ISet<string> presentKeys,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var (aliases, fallbacks) = ResolveTiers(presentKeys, overrides);
        var rows = new List<TierRow>();

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

            rows.Add(new TierRow(
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
    /// This replaces a function that rendered a whole config document and
    /// returned this line alongside it. The document is gone; the line is what
    /// anything actually wanted.
    /// </remarks>
    public static string Summarize(
        ISet<string> presentKeys, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (presentKeys.Count == 0)
            throw new InvalidOperationException(
                "ModelCatalog.Summarize called with zero provider keys. "
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
            // key was absent, so that a generated config document would still
            // list the model with no key beside it. That document is gone, and
            // the claim was never true of the request — a stage routed there
            // failed at the provider. Omitting it means the caller learns which
            // key is missing instead.
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
