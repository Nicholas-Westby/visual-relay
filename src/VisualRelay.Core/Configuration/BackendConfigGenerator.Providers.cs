namespace VisualRelay.Core.Configuration;

public static partial class BackendConfigGenerator
{
    /// <summary>Env-var → human-readable provider name.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProviderNames = new Dictionary<string, string>
    {
        ["HF_TOKEN"] = "Hugging Face",
        ["DEEPSEEK_API_KEY"] = "DeepSeek",
        ["MOONSHOT_API_KEY"] = "Moonshot",
        ["ANTHROPIC_API_KEY"] = "Anthropic",
        ["OPENAI_API_KEY"] = "OpenAI",
    };

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
