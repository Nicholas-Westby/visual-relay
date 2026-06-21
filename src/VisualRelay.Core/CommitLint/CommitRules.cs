using System.Text.RegularExpressions;

namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Single source of truth for the Conventional-Commit ruleset shared by the
/// validator (<see cref="CommitMessageValidator"/>) and the generator
/// (<c>CommitMessageSanitizer</c>), so the messages Visual Relay writes can
/// never drift from what the hook accepts. Rules are ported from ai-sorcery's
/// <c>check-commit-message.ts</c> and reconciled with this repo's fixed type set.
/// </summary>
public static partial class CommitRules
{
    /// <summary>The fixed canonical commit types. Nothing else is accepted.</summary>
    public static readonly IReadOnlyList<string> Types =
        ["feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"];

    /// <summary>Maximum subject-line length in characters (inclusive).</summary>
    public const int MaxSubjectChars = 72;

    /// <summary>Maximum number of hyphen bullets allowed in the body.</summary>
    public const int MaxBullets = 3;

    /// <summary>Maximum number of whitespace-split words allowed per bullet.</summary>
    public const int MaxBulletWords = 20;

    /// <summary>The em dash (U+2014); zero allowed anywhere in subject+body.</summary>
    public const char EmDash = '—';

    /// <summary>The hyphen-bullet prefix every non-blank body line must use.</summary>
    public const string BulletPrefix = "- ";

    /// <summary>
    /// Matches a valid subject prefix: a canonical type, optional
    /// <c>(scope)</c> where scope is <c>[a-z0-9._-]+</c>, optional <c>!</c>,
    /// then <c>: </c> and a non-empty description. Anchored at the start.
    /// </summary>
    public static Regex SubjectPrefix { get; } = BuildSubjectPrefix();

    /// <summary>
    /// Captures the prefix and the first non-space character after it, so the
    /// lowercase-after-prefix rule can inspect that character. Mirrors the
    /// reference regex <c>^(\w+(?:\([^)]+\))?:)\s*(\S)</c>.
    /// </summary>
    public static Regex FirstCharAfterPrefix { get; } = BuildFirstCharAfterPrefix();

    /// <summary>
    /// Matches a trailer line: <c>^[A-Z][A-Za-z-]+:\s.+$</c>. The trailing
    /// trailer block is stripped before any rule runs (so trailers are exempt).
    /// </summary>
    public static Regex TrailerLine { get; } = BuildTrailerLine();

    /// <summary>
    /// Matches a path-like token: a <c>/</c> with a non-whitespace character on
    /// both sides (<c>(?&lt;!\s)/(?!\s)</c>).
    /// </summary>
    public static Regex PathToken { get; } = BuildPathToken();

    /// <summary>The rules-doc path the hook output points at.</summary>
    public const string RulesDoc = "docs/commit-messages.md";

    private static Regex BuildSubjectPrefix()
    {
        var types = string.Join('|', Types);
        return new Regex($"^(?:{types})(?:\\([a-z0-9._-]+\\))?!?: \\S", RegexOptions.Compiled);
    }

    [GeneratedRegex(@"^(\w+(?:\([^)]+\))?:)\s*(\S)")]
    private static partial Regex BuildFirstCharAfterPrefix();

    [GeneratedRegex(@"^[A-Z][A-Za-z-]+:\s.+$")]
    private static partial Regex BuildTrailerLine();

    [GeneratedRegex(@"(?<!\s)/(?!\s)")]
    private static partial Regex BuildPathToken();
}
