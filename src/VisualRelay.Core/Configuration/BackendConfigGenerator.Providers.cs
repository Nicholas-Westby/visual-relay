namespace VisualRelay.Core.Configuration;

public static partial class BackendConfigGenerator
{
    /// <summary>
    /// Every provider the backend recognises, as (env-var, display-name) pairs.
    /// Order is presentational only — nothing resolves by position here, and the
    /// settings panel keeps its own ordering in
    /// <c>MainWindowViewModel.AllProviderKeys</c>.
    /// </summary>
    private static readonly (string Key, string Name)[] Providers =
    [
        ("ZAI_API_KEY", "Z.AI"),
        ("HF_TOKEN", "Hugging Face"),
        ("DEEPSEEK_API_KEY", "DeepSeek"),
        ("MOONSHOT_API_KEY", "Moonshot"),
        ("ANTHROPIC_API_KEY", "Anthropic"),
        ("OPENAI_API_KEY", "OpenAI"),
    ];

    /// <summary>Env-var → human-readable provider name.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProviderNames =
        Providers.ToDictionary(p => p.Key, p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// Every provider env-var name. Single source of truth for
    /// the "which keys are set?" probes in <c>BackendConfigStep</c> and
    /// <c>VisualRelay.GenBackendConfig</c>, which each used to carry their own
    /// hand-maintained copy and could drift apart silently.
    /// </summary>
    public static readonly IReadOnlyList<string> ProviderKeyNames =
        [.. Providers.Select(p => p.Key)];

    /// <summary>
    /// Returns the human-readable provider serving <paramref name="model"/>, or
    /// <c>null</c> for unknown models. Deliberately does not route through
    /// <see cref="GetRequiredKey"/>, whose defensive unknown → <c>HF_TOKEN</c>
    /// default would label placeholders like <c>"(key missing)"</c> as Hugging Face.
    /// </summary>
    public static string? ProviderFor(string model)
    {
        if (model == "fallback") return ProviderNames["HF_TOKEN"];
        if (ModelToKey.TryGetValue(model, out var key)) return ProviderNames[key];
        if (ModelToRequiredKey.TryGetValue(model, out key)) return ProviderNames[key];
        return null;
    }
}
