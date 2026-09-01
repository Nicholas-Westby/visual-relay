using VisualRelay.Core.Configuration;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers for the catalog guards.
/// <para>
/// These used to generate a LiteLLM YAML document and parse the answers back out
/// of it, because the proxy's config was the source of truth for tier
/// resolution, fallbacks and per-model timeouts. It no longer is: the catalog
/// lives in <see cref="BackendConfigGenerator"/> and
/// <see cref="ProviderRoutes"/>, and the YAML is gone. The method names and
/// shapes are kept so the guards that call them read unchanged — only where the
/// answers come from moved.
/// </para>
/// </summary>
internal static class BackendConfigGeneratorTestHelpers
{
    /// <summary>Tier to its resolved primary model, for a set of present keys.</summary>
    /// <param name="keys">Which provider keys are set.</param>
    /// <returns>Tier to primary model.</returns>
    public static Dictionary<string, string> GeneratedAliases(ISet<string> keys) =>
        GeneratedAliases(keys, null);

    /// <summary>Tier to its resolved primary model, honouring config overrides.</summary>
    /// <param name="keys">Which provider keys are set.</param>
    /// <param name="overrides">Per-tier model overrides from config.</param>
    /// <returns>Tier to primary model.</returns>
    public static Dictionary<string, string> GeneratedAliases(
        ISet<string> keys, IReadOnlyDictionary<string, string>? overrides) =>
        BackendConfigGenerator.ResolveChains(keys, overrides)
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value[0], StringComparer.Ordinal);

    /// <summary>Tier to the models it falls through to after its primary.</summary>
    /// <param name="keys">Which provider keys are set.</param>
    /// <returns>Tier to its fallback chain.</returns>
    public static Dictionary<string, List<string>> GeneratedFallbacks(ISet<string> keys) =>
        GeneratedFallbacks(keys, null);

    /// <summary>Tier to the models it falls through to, honouring overrides.</summary>
    /// <param name="keys">Which provider keys are set.</param>
    /// <param name="overrides">Per-tier model overrides from config.</param>
    /// <returns>Tier to its fallback chain.</returns>
    public static Dictionary<string, List<string>> GeneratedFallbacks(
        ISet<string> keys, IReadOnlyDictionary<string, string>? overrides) =>
        BackendConfigGenerator.ResolveChains(keys, overrides)
            .Where(pair => pair.Value.Count > 1)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Skip(1).ToList(),
                StringComparer.Ordinal);

    /// <summary>
    /// Whether a tier's chain ends at the fallback floor. Every chain must:
    /// that is the always-available model of last resort.
    /// </summary>
    /// <param name="tier">The tier to check.</param>
    /// <param name="fallbacks">The resolved fallback chains.</param>
    /// <returns>True when the chain terminates in the floor model.</returns>
    public static bool ChainTerminatesInFallback(string tier, Dictionary<string, List<string>> fb) =>
        fb.TryGetValue(tier, out var chain)
        && chain.Count > 0
        && string.Equals(chain[^1], BackendConfigGenerator.FallbackFloorModel, StringComparison.Ordinal);

    /// <summary>The concrete id a provider is sent for a catalog alias.</summary>
    /// <param name="modelName">The catalog alias.</param>
    /// <returns>The upstream model id, or <c>null</c> when nothing routes it.</returns>
    public static string? UpstreamModel(string modelName) =>
        ProviderRoutes.For(modelName)?.UpstreamModel;

    /// <summary>Catalog alias to its total request budget in seconds.</summary>
    /// <returns>One entry per routed model.</returns>
    public static Dictionary<string, int> ModelTimeouts() =>
        ProviderRoutes.Aliases.ToDictionary(
            alias => alias,
            alias => (int)ProviderRoutes.For(alias)!.Timeouts.Total.TotalSeconds,
            StringComparer.Ordinal);

    /// <summary>Every model the catalog can route to.</summary>
    /// <returns>The routable model names.</returns>
    public static HashSet<string> RoutableModels() =>
        ProviderRoutes.Aliases.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Compares the routable catalog against pricing, chain and selectable
    /// copies, returning ordered problem strings. One code path serves both the
    /// guard and its negative control, so a control that passes proves the guard
    /// can fail.
    /// </summary>
    /// <param name="routable">The routable model set to compare against.</param>
    /// <param name="pricingKeys">Priced model names, when checking pricing.</param>
    /// <param name="chains">Tier chains, when checking chain reachability.</param>
    /// <param name="selectable">Selectable models per tier, when checking those.</param>
    /// <returns>Ordered problem descriptions; empty when consistent.</returns>
    public static IReadOnlyList<string> FindCatalogProblems(
        HashSet<string> routable,
        IEnumerable<string>? pricingKeys = null,
        IReadOnlyDictionary<string, List<(string Model, string RequiredKey)>>? chains = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? selectable = null)
    {
        var problems = new List<string>();

        if (pricingKeys != null)
        {
            var priced = pricingKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var model in routable.Where(m => !priced.Contains(m)).OrderBy(m => m, StringComparer.Ordinal))
                problems.Add($"routable but not priced: {model}");
            foreach (var model in priced.Where(m => !routable.Contains(m)).OrderBy(m => m, StringComparer.Ordinal))
                problems.Add($"priced but not routable: {model}");
        }

        if (chains != null)
        {
            foreach (var tier in chains.Keys.OrderBy(t => t, StringComparer.Ordinal))
            foreach (var (model, _) in chains[tier].OrderBy(c => c.Model, StringComparer.Ordinal))
            {
                if (model == "fallback") continue; // a tier alias, not a model
                if (!routable.Contains(model))
                    problems.Add($"chained but not routable: {model} (tier {tier})");
            }
        }

        if (selectable != null)
        {
            foreach (var tier in selectable.Keys.OrderBy(t => t, StringComparer.Ordinal))
            foreach (var model in selectable[tier].OrderBy(m => m, StringComparer.Ordinal))
                if (!routable.Contains(model))
                    problems.Add($"selectable but not routable: {model} (tier {tier})");
        }

        return problems;
    }
}
