namespace VisualRelay.Tests;

/// <summary>
/// The one header policy shared by the cassette key, the recorder and the
/// replayer. Three questions, three answers, in one place so the key and the
/// written file can never disagree about what a secret is.
/// </summary>
internal static class CassetteHeaders
{
    /// <summary>
    /// The placeholder a redacted value carries in a written cassette. Square
    /// brackets, not angle ones: JSON escapes &lt; and &gt; to \u003C/\u003E, which
    /// would leave the marker unreadable in the file and unfindable by a grep.
    /// </summary>
    public const string Redacted = "[redacted]";

    /// <summary>
    /// The ONLY request headers that participate in the key. An allowlist rather
    /// than a denylist: a header nobody thought about — a client version stamp, a
    /// routing hint, a fresh trace id — must not silently invalidate every
    /// recorded cassette. A header joins the key only when someone adds it here,
    /// which is a deliberate act that comes with a re-record.
    /// </summary>
    private static readonly string[] Allowlist = ["accept", "content-type"];

    /// <summary>Header names whose VALUE is a credential and is never persisted.</summary>
    private static readonly string[] Secrets =
    [
        "authorization", "proxy-authorization", "x-api-key", "api-key",
        "x-goog-api-key", "cookie", "set-cookie",
    ];

    /// <summary>Query-parameter names whose value is a credential.</summary>
    private static readonly string[] SecretQueryNames =
    [
        "key", "api_key", "apikey", "token", "access_token",
    ];

    /// <summary>
    /// Substrings marking a header whose value changes on every call. Keying on
    /// one would make every replay a miss; persisting one would make every
    /// re-record a diff.
    /// </summary>
    private static readonly string[] VolatileMarkers =
    [
        "request-id", "requestid", "trace-id", "traceid", "correlation-id",
        "timestamp", "nonce",
    ];

    /// <summary>Lower-cases and trims a name so the policy is case-insensitive.</summary>
    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>True when the header's value is a credential.</summary>
    /// <param name="name">The header name, in any casing.</param>
    /// <returns>Whether the value must be redacted before it is written.</returns>
    public static bool IsSecret(string name) =>
        Secrets.Contains(Normalize(name), StringComparer.Ordinal);

    /// <summary>True when the query parameter's value is a credential.</summary>
    /// <param name="name">The parameter name, in any casing.</param>
    /// <returns>Whether the value must be redacted before it is keyed or written.</returns>
    public static bool IsSecretQueryName(string name) =>
        SecretQueryNames.Contains(Normalize(name), StringComparer.Ordinal);

    /// <summary>True when the header carries a per-call value (id, timestamp, nonce).</summary>
    private static bool IsVolatile(string name)
    {
        var normalized = Normalize(name);
        return normalized is "date"
            || VolatileMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The read-side filter: the allowlisted headers that participate in the key,
    /// name-normalized, value-trimmed and ordered so header order cannot change
    /// the key. Secrets and volatile names are refused even if allowlisted.
    /// </summary>
    /// <param name="headers">The request headers as supplied by the caller.</param>
    /// <returns>The keyed subset, ordered by name.</returns>
    public static SortedDictionary<string, string> Keyed(IReadOnlyDictionary<string, string> headers)
    {
        var keyed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            var normalized = Normalize(name);
            if (!Allowlist.Contains(normalized, StringComparer.Ordinal)) continue;
            if (IsSecret(normalized) || IsVolatile(normalized)) continue;
            keyed[normalized] = value.Trim();
        }

        return keyed;
    }

    /// <summary>
    /// The write-side filter: a secret's value becomes <see cref="Redacted"/> and a
    /// volatile header is dropped, so a committed cassette can carry no credential
    /// and does not churn between re-records.
    /// </summary>
    /// <param name="headers">The headers about to be written to a cassette.</param>
    /// <returns>The redacted headers, ordered by name.</returns>
    public static SortedDictionary<string, string> Redact(IReadOnlyDictionary<string, string> headers)
    {
        var redacted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            var normalized = Normalize(name);
            if (IsVolatile(normalized)) continue;
            redacted[normalized] = IsSecret(normalized) ? Redacted : value;
        }

        return redacted;
    }
}
