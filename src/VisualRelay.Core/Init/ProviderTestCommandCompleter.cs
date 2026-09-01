using System.Text.Json;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Core.Init;

/// <summary>
/// Asks a provider directly for a one-line completion, for the init-time test
/// command guess.
/// <para>
/// This replaces a raw <c>HttpClient</c> POST to a local endpoint — the last
/// place in the project that called a model over HTTP by itself, and the only
/// one with no test coverage at all. It now goes through the same transport
/// seam, the same route catalog and the same key resolution as the agent loop,
/// so it can be exercised offline.
/// </para>
/// </summary>
/// <param name="transport">The transport to send through.</param>
/// <param name="environment">Where provider keys are resolved from.</param>
/// <param name="tier">Which tier to ask; the guess is cheap and short.</param>
public sealed class ProviderTestCommandCompleter(
    IProviderTransport transport,
    IEnvironmentAccessor environment,
    string tier = "cheap")
{
    private readonly ProviderKeyResolver _keys = new(environment);

    /// <summary>
    /// Sends one prompt and returns the model's text.
    /// </summary>
    /// <param name="prompt">The prompt to send.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The model's answer, or empty when nothing could be asked.</returns>
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (FirstRoute() is not { } route) return string.Empty;

        var client = new ChatCompletionClient(transport, route.Timeouts);
        var body = ChatRequestBuilder.Build(
            [new ChatMessage("user", prompt)],
            new ChatRequestOptions(route.UpstreamModel, Stream: true),
            ProviderCapabilityCatalog.For(route.ProviderName));

        var completion = await client.StreamAsync(
            new ProviderRequest(
                HttpMethod.Post,
                route.Endpoint,
                route.Headers(_keys.Resolve(route.ApiKeyEnvVar) ?? string.Empty),
                body),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // A guess that fails is not worth surfacing: the caller falls back to
        // marker-based detection, which is what runs on every machine with no
        // provider key at all.
        return completion.Outcome == CompletionOutcome.Completed ? completion.Content : string.Empty;
    }

    /// <summary>The first model in the tier's chain whose key is present.</summary>
    private ProviderRoute? FirstRoute()
    {
        var present = _keys.PresentKeys();
        if (present.Count == 0) return null;

        return ModelCatalog.ResolveChains(present).TryGetValue(tier, out var models)
            ? models.Select(ProviderRoutes.For).OfType<ProviderRoute>()
                .FirstOrDefault(route => present.Contains(route.ApiKeyEnvVar))
            : null;
    }

    /// <summary>
    /// Reads the content out of a non-streaming chat response, for a caller that
    /// already has one buffered.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <returns>The message content, or empty when the shape is unexpected.</returns>
    public static string ReadContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return string.Empty;
        }
    }
}
