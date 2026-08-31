namespace VisualRelay.Core.Llm;

/// <summary>
/// One HTTP request to a model provider, at the wire level. The transport seam
/// deals in bytes rather than parsed model messages so that SSE framing, delta
/// accumulation and usage extraction stay testable without a transport, and so
/// a recorded cassette is a faithful record of what actually crossed the wire.
/// </summary>
/// <param name="Method">HTTP method; every provider call today is POST.</param>
/// <param name="Uri">Absolute endpoint URI, including the path.</param>
/// <param name="Headers">
/// Request headers to send. Authorization lives here and is elided before a
/// cassette is written or keyed, so a recorded exchange carries no secret.
/// </param>
/// <param name="Body">The serialized JSON request body.</param>
public sealed record ProviderRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

/// <summary>
/// A complete, buffered response from a provider. Used for the non-streaming
/// path and for any error response, including one that arrives mid-stream.
/// </summary>
/// <param name="StatusCode">
/// The HTTP status. Never trust it alone: Z.AI returns 200 with an error body,
/// so callers must also probe the payload for an <c>error</c> key.
/// </param>
/// <param name="Headers">Response headers, lower-cased keys.</param>
/// <param name="Body">The raw response body. Not assumed to be JSON.</param>
public sealed record ProviderResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
