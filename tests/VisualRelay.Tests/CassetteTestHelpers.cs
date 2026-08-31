using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Request builders shared by the cassette suites, so each test states only the
/// thing it is about (a changed field, a reordered header) rather than
/// re-spelling a chat-completions request every time.
/// </summary>
internal static class CassetteTestHelpers
{
    private const string Endpoint = "https://api.example.test/v1/chat/completions";

    /// <summary>A chat-completions body carrying one user message.</summary>
    /// <param name="content">The user message text.</param>
    /// <param name="model">The model name the body names.</param>
    /// <returns>The JSON body.</returns>
    public static string ChatBody(string content, string model = "test-model") =>
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{content}}"}]}""";

    /// <summary>A POST request to a provider endpoint.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="headers">The request headers; empty when omitted.</param>
    /// <param name="uri">The endpoint; the canonical chat-completions URL when omitted.</param>
    /// <returns>The wire-level request.</returns>
    public static ProviderRequest Post(
        string body,
        IReadOnlyDictionary<string, string>? headers = null,
        string uri = Endpoint) =>
        new(HttpMethod.Post, new Uri(uri), headers ?? new Dictionary<string, string>(StringComparer.Ordinal), body);
}
