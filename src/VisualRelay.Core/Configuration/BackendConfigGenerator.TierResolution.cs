namespace VisualRelay.Core.Configuration;

public static partial class BackendConfigGenerator
{
    private static IReadOnlyDictionary<string, string>? _defaultTierResolution;

    /// <summary>Default tier-alias → concrete model resolution (head of each
    /// chain; the "fallback" pseudo-model maps to the HF floor). Used to price
    /// reports whose recorded model is a tier alias. Lazily built on first access:
    /// a field initializer here would read <see cref="Chains"/>, whose own
    /// initializer lives in another partial file, and cross-file static
    /// initializer order is unspecified. The benign data race (two threads may
    /// build identical dictionaries) is acceptable.</summary>
    public static IReadOnlyDictionary<string, string> DefaultTierResolution =>
        _defaultTierResolution ??= Chains.ToDictionary(
            kv => kv.Key,
            kv => kv.Value[0].Model == FallbackTier ? FallbackFloorModel : kv.Value[0].Model,
            StringComparer.Ordinal);
}
