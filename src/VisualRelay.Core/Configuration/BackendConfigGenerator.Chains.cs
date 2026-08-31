namespace VisualRelay.Core.Configuration;

/// <summary>
/// Tier resolution, exposed as ordered model chains. The proxy used to perform
/// this from generated YAML; the request is issued in process now, so the
/// resolution is read directly from the same catalog structures.
/// </summary>
public static partial class BackendConfigGenerator
{
    /// <summary>
    /// The ordered model chain for each tier: the primary first, then each
    /// fallback in turn. This is the resolution the proxy used to perform, made
    /// directly available now that the request is issued in process.
    /// </summary>
    /// <param name="presentKeys">Which provider keys are set.</param>
    /// <param name="overrides">Per-tier model overrides from config.</param>
    /// <returns>Tier name to its ordered chain of concrete model names.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveChains(
        ISet<string> presentKeys, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var (aliases, fallbacks) = ResolveTiers(presentKeys, overrides);
        var chains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (tier, primary) in aliases)
        {
            var chain = new List<string> { primary };
            if (fallbacks.TryGetValue(tier, out var rest)) chain.AddRange(rest);

            // A hop naming a tier rather than a model resolves one more step, so
            // the caller only ever sees concrete models it can actually send to.
            var expanded = new List<string>();
            foreach (var hop in chain)
            {
                if (aliases.TryGetValue(hop, out var concrete) && !string.Equals(hop, concrete, StringComparison.Ordinal))
                    expanded.Add(concrete);
                else if (hop == FallbackTier)
                    expanded.Add(FallbackFloorModel);
                else
                    expanded.Add(hop);
            }

            chains[tier] = expanded.Distinct(StringComparer.Ordinal).ToList();
        }

        return chains;
    }
}
