using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Turns a <see cref="ProviderRequest"/> into the SHA-256 key its cassette is
/// filed under, via a canonical JSON form of
/// <c>(version, method, path, model, allowlisted headers, body)</c>.
/// <para>
/// The canonical form exists so that two requests which mean the same thing key
/// the same: header order and JSON object-key order are normalized away, secrets
/// and per-call ids never reach the hash, and object keys are sorted recursively.
/// Array order is NOT sorted — order is semantic for <c>messages</c>, and a
/// reordered conversation is a different request.
/// </para>
/// <para>
/// <b>Versioning.</b> <see cref="Version"/> is part of the hashed input. Any change
/// to what this class considers canonical — a new allowlisted header, a different
/// body normalization — must bump it. Bumping changes every key at once, which
/// turns a canonicalizer change into a deliberate, visible re-record instead of a
/// silent mass miss where every cassette quietly stops matching.
/// </para>
/// </summary>
internal static class CassetteKey
{
    /// <summary>
    /// The canonicalizer version, hashed into every key. Bump it whenever the
    /// canonical form changes; the cassettes must then be re-recorded.
    /// </summary>
    public const int Version = 1;

    /// <summary>The SHA-256 hex key this request is filed under.</summary>
    /// <param name="request">The wire-level request being keyed.</param>
    /// <param name="version">The canonicalizer version; defaults to <see cref="Version"/>.</param>
    /// <returns>Lower-case hex SHA-256 over the canonical form.</returns>
    public static string Compute(ProviderRequest request, int version = Version) =>
        Hash(Canonical(request, version).ToJsonString());

    /// <summary>
    /// The canonical request: the exact document that gets hashed, and the same
    /// document a cassette stores so a miss can be diffed field by field.
    /// </summary>
    /// <param name="request">The wire-level request being canonicalized.</param>
    /// <param name="version">The canonicalizer version; defaults to <see cref="Version"/>.</param>
    /// <returns>A fresh <see cref="JsonObject"/> in canonical property order.</returns>
    public static JsonObject Canonical(ProviderRequest request, int version = Version)
    {
        var body = ParseBody(request.Body);
        var model = ModelOf(body);
        return new JsonObject
        {
            ["version"] = version,
            ["method"] = request.Method.Method.ToUpperInvariant(),
            ["path"] = CanonicalPath(request.Uri),
            ["model"] = model is null ? null : JsonValue.Create(model),
            ["headers"] = KeyedHeaders(request.Headers),
            ["body"] = Sorted(body),
        };
    }

    /// <summary>Lower-case hex SHA-256 of a canonical form's UTF-8 bytes.</summary>
    /// <param name="canonical">The canonical JSON text.</param>
    /// <returns>The 64-character hex digest.</returns>
    public static string Hash(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// The body as JSON when it parses, and as a verbatim JSON string when it does
    /// not — a provider that takes form or plain-text bodies still keys stably.
    /// </summary>
    private static JsonNode? ParseBody(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(body);
        }
    }

    /// <summary>The body's <c>model</c> property, when the body is a JSON object with a string one.</summary>
    private static string? ModelOf(JsonNode? body) =>
        body is JsonObject obj
        && obj.TryGetPropertyValue("model", out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var model)
            ? model
            : null;

    /// <summary>
    /// Path plus, when present, the query with its pairs sorted and any
    /// credential-bearing value redacted — so a key in the query neither reaches
    /// the hash nor the cassette, and two paths differing only by query cannot
    /// collide onto one cassette.
    /// </summary>
    private static string CanonicalPath(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return uri.OriginalString;

        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return uri.AbsolutePath;

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(RedactPair)
            .Order(StringComparer.Ordinal);
        return $"{uri.AbsolutePath}?{string.Join('&', pairs)}";
    }

    private static string RedactPair(string pair)
    {
        var split = pair.IndexOf('=', StringComparison.Ordinal);
        if (split < 0) return pair;

        var name = pair[..split];
        return CassetteHeaders.IsSecretQueryName(name)
            ? $"{name}={CassetteHeaders.Redacted}"
            : pair;
    }

    private static JsonObject KeyedHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var node = new JsonObject();
        foreach (var (name, value) in CassetteHeaders.Keyed(headers)) node[name] = value;
        return node;
    }

    /// <summary>Recursively sorts object keys; array order is left alone.</summary>
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => SortedObject(obj),
        JsonArray array => SortedArray(array),
        _ => node?.DeepClone(),
    };

    private static JsonObject SortedObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var property in obj.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            sorted[property.Key] = Sorted(property.Value?.DeepClone());
        return sorted;
    }

    private static JsonArray SortedArray(JsonArray array)
    {
        var sorted = new JsonArray();
        foreach (var item in array) sorted.Add(Sorted(item?.DeepClone()));
        return sorted;
    }
}
