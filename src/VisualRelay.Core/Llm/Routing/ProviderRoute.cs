namespace VisualRelay.Core.Llm.Routing;

/// <summary>
/// Everything needed to send one model a request: where it lives, what the
/// provider calls it, which key opens it, how big its context is and how long to
/// wait for it.
/// <para>
/// This replaces the generated config document. The catalog alias (for example
/// <c>hf-qwen3-coder-next</c>) is what the rest of Visual Relay names; the
/// upstream id is what the provider actually answers to, and the two differ on
/// every Hugging Face route.
/// </para>
/// </summary>
/// <param name="Alias">The name the catalog, pricing and config all use.</param>
/// <param name="ProviderName">
/// The provider's display name, matching the key catalog so capabilities and
/// pricing line up.
/// </param>
/// <param name="Endpoint">The chat-completions endpoint.</param>
/// <param name="UpstreamModel">
/// The id the provider expects. Hugging Face is case-sensitive on the way in and
/// lower-cases it on the way out, so this value is authoritative and a response
/// id must never be echoed back into a retry.
/// </param>
/// <param name="ApiKeyEnvVar">Which environment variable holds the key.</param>
/// <param name="ContextWindow">
/// The window in tokens, which compaction measures against. Known for every
/// route, so nothing needs to learn it adaptively.
/// </param>
/// <param name="Timeouts">The four budgets for this route.</param>
public sealed record ProviderRoute(
    string Alias,
    string ProviderName,
    Uri Endpoint,
    string UpstreamModel,
    string ApiKeyEnvVar,
    int ContextWindow,
    ProviderTimeouts Timeouts)
{
    /// <summary>
    /// The request headers for this route, given the key.
    /// </summary>
    /// <param name="apiKey">The provider key.</param>
    /// <returns>Headers to send.</returns>
    public IReadOnlyDictionary<string, string> Headers(string apiKey) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + apiKey,
            ["Accept"] = "text/event-stream",
        };
}
