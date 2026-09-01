using VisualRelay.Core.Configuration;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Core.Costs;

public static partial class RelayCostEstimator
{
    /// <summary>
    /// The price card for a stage, from the model that actually served it.
    /// </summary>
    /// <param name="servedModel">The id the provider echoed back, if any.</param>
    /// <param name="tierAlias">The tier the stage asked for.</param>
    /// <returns>The pricing, or <c>null</c> when nothing claims the stage.</returns>
    /// <remarks>
    /// A response names the UPSTREAM id, which is not what the price table is
    /// keyed on: Moonshot answers to <c>kimi-k2.7-code</c> while the table says
    /// <c>kimi-k2</c>. The lookup used to try the served id alone and report the
    /// stage as free on a miss, which is what five of the nine routes did.
    /// </remarks>
    private static ModelPricing? ResolvePricing(string? servedModel, string tierAlias)
    {
        string?[] keys =
        [
            servedModel is { Length: > 0 } ? servedModel : null,
            ProviderRoutes.AliasForServedModel(servedModel),
            tierAlias,
        ];

        foreach (var key in keys)
        {
            if (key is not { Length: > 0 }) continue;
            if (RelayPricing.Default.TryGetValue(key, out var direct)) return direct;
            if (BackendConfigGenerator.DefaultTierResolution.TryGetValue(key, out var concrete)
                && RelayPricing.Default.TryGetValue(concrete, out var viaTier)) return viaTier;
        }

        return null;
    }
}
