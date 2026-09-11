namespace VisualRelay.Domain;

/// <summary>
/// How the Author-tests stage is gated in this repository, written by bootstrap
/// from the detected test layout and editable afterwards.
/// </summary>
/// <param name="DetectedLanguages">
/// Informational: the languages init found, so an operator can see why the
/// defaults are what they are. Ordered by file count descending, then id.
/// </param>
/// <param name="InlineTestExtensions">
/// Extensions whose files may legitimately carry tests next to the
/// implementation. Lowercase, leading dot, distinct, sorted ordinal. Every file
/// the model lists as a test file is kept whatever its extension; an
/// inline-capable extension only changes how the gate judges it.
/// </param>
/// <param name="DiffAudit">
/// One of <see cref="DiffAuditAuto"/>, <see cref="DiffAuditAlways"/> or
/// <see cref="DiffAuditOff"/>.
/// </param>
public sealed record AuthorTestsConfig(
    IReadOnlyList<string> DetectedLanguages,
    IReadOnlyList<string> InlineTestExtensions,
    string DiffAudit)
{
    /// <summary>Audit the Stage 5 diff only when an inline-capable or suspect file was edited.</summary>
    public const string DiffAuditAuto = "auto";

    /// <summary>Audit every Stage 5 diff.</summary>
    public const string DiffAuditAlways = "always";

    /// <summary>Never audit the Stage 5 diff.</summary>
    public const string DiffAuditOff = "off";

    /// <summary>Nothing detected, no inline extensions, audit on demand.</summary>
    public static readonly AuthorTestsConfig Default = new([], [], DiffAuditAuto);

    /// <summary>
    /// Whether <paramref name="value"/> is one of the three audit modes, spelled
    /// exactly as the config key accepts it.
    /// </summary>
    /// <param name="value">The configured value, possibly null.</param>
    /// <returns>True when the value is a recognized mode.</returns>
    public static bool IsValidDiffAudit(string? value) =>
        value is DiffAuditAuto or DiffAuditAlways or DiffAuditOff;

    /// <summary>
    /// Value equality over the two lists as well as the mode: a record's
    /// generated <c>Equals</c> compares the list references, which would make two
    /// configs holding the same strings unequal.
    /// </summary>
    /// <param name="other">The config to compare with.</param>
    /// <returns>True when both lists and the mode match.</returns>
    public bool Equals(AuthorTestsConfig? other) =>
        other is not null
        && string.Equals(DiffAudit, other.DiffAudit, StringComparison.Ordinal)
        && DetectedLanguages.SequenceEqual(other.DetectedLanguages, StringComparer.Ordinal)
        && InlineTestExtensions.SequenceEqual(other.InlineTestExtensions, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DiffAudit, StringComparer.Ordinal);
        foreach (var language in DetectedLanguages)
            hash.Add(language, StringComparer.Ordinal);
        foreach (var extension in InlineTestExtensions)
            hash.Add(extension, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
