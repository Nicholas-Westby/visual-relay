namespace VisualRelay.Core.Llm.Routing;

/// <summary>
/// Where every model in the catalog actually lives. This is the single source of
/// truth that <c>tools/backend/litellm-config.yaml</c> used to be: the proxy's
/// <c>model_list</c>, its per-model timeouts and its provider routing, expressed
/// in the language the rest of the project is written in.
/// <para>
/// Every remaining provider speaks OpenAI-compatible
/// <c>/chat/completions</c>, which is why one route shape covers all four and
/// why no SDK or adapter layer is needed to normalize between them.
/// </para>
/// </summary>
public static class ProviderRoutes
{
    private static readonly Uri DeepSeek = new("https://api.deepseek.com/chat/completions");
    private static readonly Uri Zai = new("https://api.z.ai/api/paas/v4/chat/completions");
    private static readonly Uri Moonshot = new("https://api.moonshot.ai/v1/chat/completions");
    private static readonly Uri HuggingFace = new("https://router.huggingface.co/v1/chat/completions");

    /// <summary>
    /// Budgets for a reasoning model that legitimately thinks for a long time.
    /// The total is generous; the inter-chunk idle budget is what actually
    /// catches a wedge, and reasoning streams as it is produced.
    /// </summary>
    private static readonly ProviderTimeouts Reasoning = new(
        Connect: TimeSpan.FromSeconds(10),
        TimeToFirstByte: TimeSpan.FromSeconds(60),
        InterChunkIdle: TimeSpan.FromSeconds(60),
        Total: TimeSpan.FromSeconds(900));

    /// <summary>Budgets for a fast model, where a long silence is a fault.</summary>
    private static readonly ProviderTimeouts Fast = new(
        Connect: TimeSpan.FromSeconds(10),
        TimeToFirstByte: TimeSpan.FromSeconds(30),
        InterChunkIdle: TimeSpan.FromSeconds(45),
        Total: TimeSpan.FromSeconds(600));

    private static readonly IReadOnlyDictionary<string, ProviderRoute> ByAlias =
        new Dictionary<string, ProviderRoute>(StringComparer.Ordinal)
        {
            ["glm-5.3-flash"] = new(
                "glm-5.3-flash", "Z.AI", Zai, "glm-5.3-flash",
                "ZAI_API_KEY", 1_000_000, Reasoning),

            // The same upstream model over Hugging Face, provider-pinned to
            // zai-org, for a user who has only an HF token.
            ["hf-glm-5.3-flash"] = new(
                "hf-glm-5.3-flash", "Hugging Face", HuggingFace, "zai-org/GLM-5.3-Flash:zai-org",
                "HF_TOKEN", 1_000_000, Reasoning),

            ["kimi-k2"] = new(
                "kimi-k2", "Moonshot", Moonshot, "kimi-k2.7-code",
                "MOONSHOT_API_KEY", 256_000, Reasoning),

            ["deepseek-v4-pro"] = new(
                "deepseek-v4-pro", "DeepSeek", DeepSeek, "deepseek-v4-pro",
                "DEEPSEEK_API_KEY", 128_000, Fast),

            ["deepseek-v4-flash"] = new(
                "deepseek-v4-flash", "DeepSeek", DeepSeek, "deepseek-v4-flash",
                "DEEPSEEK_API_KEY", 128_000, Fast),

            ["deepseek-v4-flash-vision-exp"] = new(
                "deepseek-v4-flash-vision-exp", "DeepSeek", DeepSeek, "deepseek-v4-flash-vision-exp",
                "DEEPSEEK_API_KEY", 128_000, Fast),

            ["hf-qwen3-coder-next"] = new(
                "hf-qwen3-coder-next", "Hugging Face", HuggingFace,
                "Qwen/Qwen3-Coder-480B-A35B-Instruct:novita",
                "HF_TOKEN", 256_000, Fast),

            // The two vision routes are UNPINNED, so Hugging Face picks the
            // serving host per request and the envelope moves with it: the 235B
            // is 131k tokens on one host and 262k on another. The smaller window
            // is recorded, for the same reason the dearer price is: assuming the
            // roomier host would silently over-fill the context on the other one.
            ["hf-qwen3-vl-235b"] = new(
                "hf-qwen3-vl-235b", "Hugging Face", HuggingFace, "Qwen/Qwen3-VL-235B-A22B-Instruct",
                "HF_TOKEN", 131_072, Fast),

            ["hf-qwen3-vl-30b"] = new(
                "hf-qwen3-vl-30b", "Hugging Face", HuggingFace, "Qwen/Qwen3-VL-30B-A3B-Instruct",
                "HF_TOKEN", 131_072, Fast),
        };

    /// <summary>Every routable model alias.</summary>
    public static IReadOnlyCollection<string> Aliases => (IReadOnlyCollection<string>)ByAlias.Keys;

    /// <summary>
    /// The route for a model alias.
    /// </summary>
    /// <param name="alias">The catalog alias.</param>
    /// <returns>The route, or <c>null</c> when nothing serves that name.</returns>
    public static ProviderRoute? For(string alias) => ByAlias.GetValueOrDefault(alias);

    /// <summary>
    /// The catalog alias for a model id a provider echoed back in its response.
    /// </summary>
    /// <param name="servedModel">The id the provider named.</param>
    /// <returns>The alias, or <c>null</c> when no route claims that id.</returns>
    /// <remarks>
    /// Pricing is keyed on the alias, but a response names the UPSTREAM id, and
    /// the two differ on five routes — <c>kimi-k2</c> answers to
    /// <c>kimi-k2.7-code</c>. Without this the estimator looked the upstream id
    /// up in the price table, missed, and reported the stage as costing nothing.
    /// Hugging Face lower-cases the id on the way out and may drop the provider
    /// pin, so the match ignores case and tolerates a missing suffix.
    /// </remarks>
    public static string? AliasForServedModel(string? servedModel)
    {
        if (string.IsNullOrWhiteSpace(servedModel)) return null;

        foreach (var (alias, route) in ByAlias)
            if (string.Equals(route.UpstreamModel, servedModel, StringComparison.Ordinal))
                return alias;

        var served = Unpinned(servedModel);
        foreach (var (alias, route) in ByAlias)
            if (string.Equals(Unpinned(route.UpstreamModel), served, StringComparison.OrdinalIgnoreCase))
                return alias;

        return null;
    }

    /// <summary>An upstream id with any <c>:provider</c> pin removed.</summary>
    private static string Unpinned(string model)
    {
        var pin = model.LastIndexOf(':');
        return pin > 0 ? model[..pin] : model;
    }

    /// <summary>
    /// The capabilities of whichever provider serves a model alias.
    /// </summary>
    /// <param name="alias">The catalog alias.</param>
    /// <returns>The measured capabilities, conservative for an unknown alias.</returns>
    public static ProviderCapabilities CapabilitiesFor(string alias) =>
        ProviderCapabilityCatalog.For(For(alias)?.ProviderName ?? string.Empty);
}
