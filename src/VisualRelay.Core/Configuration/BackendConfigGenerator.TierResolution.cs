namespace VisualRelay.Core.Configuration;

public static partial class BackendConfigGenerator
{
    /// <summary>Default tier-alias → concrete model resolution (head of each
    /// chain; the "fallback" pseudo-model maps to the HF floor). Used to price
    /// reports whose recorded model is a tier alias.</summary>
    public static IReadOnlyDictionary<string, string> DefaultTierResolution { get; } =
        Chains.ToDictionary(
            kv => kv.Key,
            kv => kv.Value[0].Model == FallbackTier ? FallbackFloorModel : kv.Value[0].Model,
            StringComparer.Ordinal);
}
