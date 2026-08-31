using System.Text.RegularExpressions;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that scans committed HTTP cassettes for material that must never
/// be checked in. Cassettes are recorded against live providers, so the recorder
/// redacting on write is the first line and this guard is the second: it reads
/// file CONTENT (no syntax trees, no compilation) and fails the build if anything
/// key-shaped survived.
/// <para>Four rules:</para>
/// <list type="number">
///   <item>The literal API-key prefixes <c>sk-</c> and <c>hf_</c>. Matched only at
///         a token boundary and only when followed by at least
///         <see cref="MinTokenTail"/> token characters, so ordinary words that
///         happen to contain the substring — <c>task-</c>, <c>disk-</c>,
///         <c>risk-</c> — do not make the guard unusable.</item>
///   <item>The literal <c>Bearer </c>. Flagged on ANY occurrence: the
///         canonicalizer elides <c>Authorization</c> entirely, so an authorization
///         header value has no business in a cassette even redacted.</item>
///   <item>Each provider key NAME from <c>.env.example</c>
///         (<see cref="ProviderKeyNames"/>) — its presence means an environment
///         dump reached the cassette.</item>
///   <item>Any run of <see cref="MinBase64UrlRun"/>+ base64url characters that is
///         not under a known-safe JSON field (<see cref="DefaultSafeFieldNames"/>,
///         overridable per call).</item>
/// </list>
/// <para>Plus the live values of any provider key currently exported in the
/// environment. Those are compared but NEVER echoed: a live-value violation
/// reports the key name and the location only, because guard failures land in CI
/// logs.</para>
/// <para>The guard is a Tier-2 guard-as-test only: it is not wired into
/// <c>./visual-relay check</c> or the guards CLI subcommand list.</para>
/// </summary>
public static class CassetteSecretGuard
{
    /// <summary>Describes a single cassette-secret violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Shortest base64url run that is treated as key-shaped.</summary>
    public const int MinBase64UrlRun = 32;

    /// <summary>
    /// Token characters that must follow <c>sk-</c> / <c>hf_</c> before the
    /// prefix counts as a key rather than as part of an ordinary word.
    /// </summary>
    public const int MinTokenTail = 8;

    /// <summary>
    /// Shortest environment value compared against cassette content. A one- or
    /// two-character export would otherwise match nearly every line.
    /// </summary>
    public const int MinLiveValueLength = 8;

    /// <summary>
    /// The provider key names carried by <c>.env.example</c>. Kept in this order
    /// so failure output is stable.
    /// </summary>
    public static IReadOnlyList<string> ProviderKeyNames { get; } =
        ["MOONSHOT_API_KEY", "DEEPSEEK_API_KEY", "ZAI_API_KEY", "HF_TOKEN"];

    /// <summary>
    /// JSON fields whose values are legitimately long base64url runs: the
    /// cassette's own content-addressed key/digest fields, and base64 payload
    /// fields. Broad fields — <c>body</c>, <c>content</c>, <c>url</c> — are
    /// deliberately absent: that is exactly where a leaked token would sit.
    /// Pass a different list to <see cref="FindViolations"/> as the cassette
    /// schema settles (a vision cassette carrying an inline base64 image should
    /// add its specific field rather than widening this default).
    /// </summary>
    public static IReadOnlyList<string> DefaultSafeFieldNames { get; } =
    [
        "key", "hash", "sha256", "digest", "cassetteKey", "requestHash", "canonicalHash",
        "chunk", "chunks", "chunkBase64", "bodyBase64", "contentBase64", "imageBase64", "b64_json",
    ];

    private static readonly string[] KeyPrefixes = ["sk-", "hf_"];

    private const string BearerLiteral = "Bearer ";

    private static readonly Regex Base64UrlRun =
        new($"[A-Za-z0-9_-]{{{MinBase64UrlRun},}}", RegexOptions.CultureInvariant);

    private static readonly Regex JsonFieldName =
        new("\"(?<name>[^\"]{1,128})\"[ \t]*:", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the live value of every <see cref="ProviderKeyNames"/> entry from the
    /// process environment at scan time. Unset, blank and implausibly short
    /// (&lt; <see cref="MinLiveValueLength"/>) values are skipped, so a machine
    /// with no keys exported simply contributes no extra rule.
    /// </summary>
    /// <returns>Key name to live value, for the keys that are set.</returns>
    public static IReadOnlyDictionary<string, string> ReadLiveKeyValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in ProviderKeyNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= MinLiveValueLength)
                values[name] = value;
        }

        return values;
    }

    /// <summary>
    /// Returns every cassette-secret violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. An empty sequence — the state before
    /// any cassette is recorded — yields nothing.
    /// </summary>
    /// <param name="files">Cassette path and full text content pairs.</param>
    /// <param name="liveKeyValues">
    /// Key name to live value, normally from <see cref="ReadLiveKeyValues"/>.
    /// <c>null</c> skips the live-value rule.
    /// </param>
    /// <param name="safeFieldNames">
    /// JSON fields exempt from the base64url rule; <c>null</c> uses
    /// <see cref="DefaultSafeFieldNames"/>. Compared case-insensitively.
    /// </param>
    public static IReadOnlyList<Violation> FindViolations(
        IEnumerable<(string Path, string Content)> files,
        IReadOnlyDictionary<string, string>? liveKeyValues = null,
        IEnumerable<string>? safeFieldNames = null)
    {
        var safe = new HashSet<string>(
            safeFieldNames ?? DefaultSafeFieldNames, StringComparer.OrdinalIgnoreCase);
        var violations = new List<Violation>();

        foreach (var (path, content) in files)
            ScanContent(path, content, liveKeyValues, safe, violations);

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static void ScanContent(
        string path,
        string content,
        IReadOnlyDictionary<string, string>? liveKeyValues,
        HashSet<string> safeFields,
        List<Violation> sink)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? carriedField = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            ScanPrefixes(path, line, lineNumber, sink);
            ScanKeyNames(path, line, lineNumber, sink);
            ScanLiveValues(path, line, lineNumber, liveKeyValues, sink);
            carriedField = ScanBase64Runs(path, line, lineNumber, safeFields, carriedField, sink);
        }
    }

    private static void ScanPrefixes(string path, string line, int lineNumber, List<Violation> sink)
    {
        foreach (var prefix in KeyPrefixes)
        {
            var from = 0;
            while (true)
            {
                var at = line.IndexOf(prefix, from, StringComparison.Ordinal);
                if (at < 0) break;
                from = at + 1;

                if (at > 0 && IsTokenChar(line[at - 1]))
                    continue; // mid-word: "task-", "disk-", "risk-"

                if (TokenTailLength(line, at + prefix.Length) < MinTokenTail)
                    continue; // a bare prefix or a redaction placeholder, not a key

                sink.Add(new Violation(path, lineNumber, $"col {at + 1}: '{prefix}' + key-shaped tail",
                    $"cassette contains the API-key prefix '{prefix}' followed by a key-shaped tail "
                    + "(the recorder must redact provider credentials before the cassette is written)"));
            }
        }

        var bearer = line.IndexOf(BearerLiteral, StringComparison.Ordinal);
        if (bearer >= 0)
        {
            sink.Add(new Violation(path, lineNumber, $"col {bearer + 1}: '{BearerLiteral.Trim()}' literal",
                "cassette contains the literal 'Bearer ' (the canonicalizer elides Authorization "
                + "entirely — no authorization header value belongs in a cassette, redacted or not)"));
        }
    }

    private static void ScanKeyNames(string path, string line, int lineNumber, List<Violation> sink)
    {
        foreach (var name in ProviderKeyNames)
        {
            var at = line.IndexOf(name, StringComparison.Ordinal);
            if (at < 0) continue;

            sink.Add(new Violation(path, lineNumber, $"col {at + 1}: '{name}'",
                $"cassette names the provider key '{name}' (an environment dump reached the cassette)"));
        }
    }

    private static void ScanLiveValues(
        string path,
        string line,
        int lineNumber,
        IReadOnlyDictionary<string, string>? liveKeyValues,
        List<Violation> sink)
    {
        if (liveKeyValues is null) return;

        foreach (var (name, value) in liveKeyValues)
        {
            if (!line.Contains(value, StringComparison.Ordinal)) continue;

            // Snippet and reason carry the key NAME and the location only — never
            // the value. Guard failures are printed into CI logs.
            sink.Add(new Violation(path, lineNumber, "<redacted>",
                $"cassette contains the live value of '{name}' currently exported in the "
                + "environment (rotate that key, then re-record the cassette with redaction on)"));
        }
    }

    private static string? ScanBase64Runs(
        string path,
        string line,
        int lineNumber,
        HashSet<string> safeFields,
        string? carriedField,
        List<Violation> sink)
    {
        var fields = JsonFieldName.Matches(line);

        foreach (Match run in Base64UrlRun.Matches(line))
        {
            // The enclosing field is the nearest property name that ENDS before the
            // run starts. A run that is itself an object key therefore falls back to
            // the parent field carried from an earlier line, which is the right
            // context for it.
            var field = carriedField;
            foreach (Match candidate in fields)
            {
                if (candidate.Index + candidate.Length > run.Index) break;
                field = candidate.Groups["name"].Value;
            }

            if (field is not null && safeFields.Contains(field)) continue;

            sink.Add(new Violation(path, lineNumber,
                $"col {run.Index + 1}: {run.Length}-char base64url run under field '{field ?? "(none)"}'",
                $"cassette carries a {run.Length}-character base64url run outside the known-safe "
                + "fields (redact it, or add its field to the safe-field list passed to the guard)"));
        }

        if (fields.Count > 0)
            carriedField = fields[^1].Groups["name"].Value;

        return carriedField;
    }

    private static int TokenTailLength(string line, int start)
    {
        var end = start;
        while (end < line.Length && IsTokenChar(line[end])) end++;
        return end - start;
    }

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-';
}
