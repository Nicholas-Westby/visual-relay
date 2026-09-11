using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Configuration;

public static partial class RelayConfigLoader
{
    /// <summary>
    /// Parses the <c>authorTests</c> object. A missing or non-object value yields
    /// <paramref name="fallback"/>; an unreadable member falls back per member
    /// rather than failing the load, because the object is advisory: getting it
    /// wrong must never make an otherwise valid repository unrunnable.
    /// </summary>
    private static AuthorTestsConfig ParseAuthorTests(JsonElement root, AuthorTestsConfig fallback)
    {
        if (!root.TryGetProperty("authorTests", out var element) || element.ValueKind != JsonValueKind.Object)
            return fallback;

        var languages = ReadStrings(element, "detectedLanguages", fallback.DetectedLanguages);
        var extensions = NormalizeExtensions(ReadStrings(element, "inlineTestExtensions", fallback.InlineTestExtensions));

        var audit = element.TryGetProperty("diffAudit", out var auditElement)
                    && auditElement.ValueKind == JsonValueKind.String
            ? auditElement.GetString()
            : fallback.DiffAudit;

        return new AuthorTestsConfig(
            languages, extensions,
            AuthorTestsConfig.IsValidDiffAudit(audit) ? audit! : AuthorTestsConfig.DiffAuditAuto);
    }

    // Present array → its string entries verbatim (non-strings dropped); absent
    // or any other kind → the fallback.
    private static IReadOnlyList<string> ReadStrings(
        JsonElement element, string name, IReadOnlyList<string> fallback)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return fallback;

        return
        [
            .. array.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value))
        ];
    }

    /// <summary>
    /// Operator-friendly spellings become the one form the scope check compares
    /// against: trimmed, lowercased, dot-prefixed, distinct and sorted ordinal,
    /// so "RS", " .rs " and ".rs" are one extension.
    /// </summary>
    private static IReadOnlyList<string> NormalizeExtensions(IEnumerable<string> values)
    {
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var trimmed = value.Trim().ToLowerInvariant();
            var dotted = trimmed.StartsWith('.') ? trimmed : "." + trimmed;
            if (dotted.Length > 1)
                normalized.Add(dotted);
        }

        return [.. normalized];
    }
}
