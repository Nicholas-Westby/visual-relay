using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// One recorded exchange — the on-disk shape of a cassette file.
/// <para>
/// The file carries the canonicalizer version, the canonical request (the same
/// document that was hashed, so a human can read what the exchange is and a miss
/// can be diffed against it field by field), and the response: status, headers,
/// and either a buffered <c>body</c> or, for a streaming exchange, the ordered
/// <c>chunks</c>.
/// </para>
/// <para>
/// <b>Chunk encoding is base64</b>, one string per chunk, in arrival order. Not a
/// text array: a provider may split an SSE event mid-UTF-8-codepoint, so decoding
/// chunk-by-chunk to text is lossy and would make the replay differ from the wire.
/// Base64 round-trips the bytes exactly; the readable version of the exchange is
/// the request block plus, for the non-streaming path, the plain-text body.
/// </para>
/// </summary>
internal sealed class CassetteRecord
{
    /// <summary>The base64 chunk encoding, named in the file so a reader need not guess.</summary>
    private const string ChunkEncoding = "base64";

    /// <summary>The SHA-256 hex key this exchange is filed under.</summary>
    public required string Key { get; init; }

    /// <summary>The <see cref="CassetteKey.Version"/> in force when this was recorded.</summary>
    public required int Version { get; init; }

    /// <summary>The provider directory, e.g. <c>deepseek</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The scenario directory, e.g. <c>tool-call-stream</c>.</summary>
    public required string Scenario { get; init; }

    /// <summary>The canonical request, redacted for writing.</summary>
    public required JsonObject Request { get; init; }

    /// <summary>The recorded HTTP status.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The recorded response headers, redacted for writing.</summary>
    public required IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }

    /// <summary>The buffered response body; empty for a streaming exchange.</summary>
    public string Body { get; private init; } = "";

    /// <summary>The ordered stream chunks, or null for a buffered exchange.</summary>
    public IReadOnlyList<byte[]>? Chunks { get; private init; }

    /// <summary>
    /// Builds the record for one exchange. The key is computed BEFORE redaction so
    /// it always equals what <see cref="CassetteKey.Compute"/> derives from the
    /// live request; redaction then applies to the stored copy only, and so can
    /// never move a cassette out from under a replay.
    /// </summary>
    /// <param name="provider">The provider directory name.</param>
    /// <param name="scenario">The scenario directory name.</param>
    /// <param name="request">The request that produced this exchange.</param>
    /// <param name="statusCode">The response status.</param>
    /// <param name="responseHeaders">The response headers, pre-redaction.</param>
    /// <param name="body">The buffered body, or empty when streaming.</param>
    /// <param name="chunks">The ordered stream chunks, or null when buffered.</param>
    /// <returns>The record to write.</returns>
    public static CassetteRecord Create(
        string provider,
        string scenario,
        ProviderRequest request,
        int statusCode,
        IReadOnlyDictionary<string, string> responseHeaders,
        string body,
        IReadOnlyList<byte[]>? chunks)
    {
        var canonical = CassetteKey.Canonical(request);
        var key = CassetteKey.Hash(canonical.ToJsonString());
        RedactRequest(canonical);

        return new CassetteRecord
        {
            Key = key,
            Version = CassetteKey.Version,
            Provider = provider,
            Scenario = scenario,
            Request = canonical,
            StatusCode = statusCode,
            ResponseHeaders = CassetteHeaders.Redact(responseHeaders),
            Body = chunks is null ? body : "",
            Chunks = chunks,
        };
    }

    /// <summary>Renders this record as the JSON document written to disk.</summary>
    /// <returns>The file's root object.</returns>
    public JsonObject ToJson()
    {
        var response = new JsonObject
        {
            ["statusCode"] = StatusCode,
            ["headers"] = HeadersNode(ResponseHeaders),
        };

        if (Chunks is null)
        {
            response["body"] = Body;
        }
        else
        {
            response["chunkEncoding"] = ChunkEncoding;
            var chunks = new JsonArray();
            foreach (var chunk in Chunks) chunks.Add(Convert.ToBase64String(chunk));
            response["chunks"] = chunks;
        }

        return new JsonObject
        {
            ["canonicalizerVersion"] = Version,
            ["key"] = Key,
            ["provider"] = Provider,
            ["scenario"] = Scenario,
            ["request"] = Request.DeepClone(),
            ["response"] = response,
        };
    }

    /// <summary>Reads a record back from a cassette file's root object.</summary>
    /// <param name="root">The parsed file.</param>
    /// <returns>The record.</returns>
    public static CassetteRecord FromJson(JsonObject root)
    {
        var response = Child(root, "response");
        var chunks = response["chunks"] as JsonArray;

        return new CassetteRecord
        {
            Key = Text(root, "key"),
            Version = (int)(root["canonicalizerVersion"] ?? throw Malformed("canonicalizerVersion")),
            Provider = Text(root, "provider"),
            Scenario = Text(root, "scenario"),
            Request = Child(root, "request"),
            StatusCode = (int)(response["statusCode"] ?? throw Malformed("response.statusCode")),
            ResponseHeaders = Headers(Child(response, "headers")),
            Body = response["body"]?.GetValue<string>() ?? "",
            Chunks = chunks is null ? null : DecodeChunks(chunks),
        };
    }

    private static void RedactRequest(JsonObject canonical)
    {
        if (canonical["headers"] is not JsonObject headers) return;

        foreach (var name in headers.Select(entry => entry.Key).ToList())
            if (CassetteHeaders.IsSecret(name))
                headers[name] = CassetteHeaders.Redacted;
    }

    private static JsonObject HeadersNode(IReadOnlyDictionary<string, string> headers)
    {
        var node = new JsonObject();
        foreach (var (name, value) in headers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            node[name] = value;
        return node;
    }

    private static IReadOnlyList<byte[]> DecodeChunks(JsonArray chunks) =>
        [.. chunks.Select(chunk =>
            Convert.FromBase64String(chunk?.GetValue<string>() ?? throw Malformed("response.chunks[]")))];

    private static IReadOnlyDictionary<string, string> Headers(JsonObject node)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in node) headers[name] = value?.GetValue<string>() ?? "";
        return headers;
    }

    private static string Text(JsonObject root, string name) =>
        (root[name] ?? throw Malformed(name)).GetValue<string>();

    private static JsonObject Child(JsonObject root, string name) =>
        root[name] as JsonObject ?? throw Malformed(name);

    private static InvalidOperationException Malformed(string field) =>
        new($"Cassette file is malformed: missing or non-object '{field}'.");
}
