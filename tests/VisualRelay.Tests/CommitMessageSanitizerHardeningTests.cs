using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The sanitizer's output must always pass the validator's <em>structural</em>
/// tier, so the messages Visual Relay generates are never rejected by the
/// commit-msg hook for a structural reason. These tests feed deliberately messy
/// raw input through <see cref="CommitMessageSanitizer.FromRawOrFallback"/> /
/// <see cref="CommitMessageSanitizer.TrySanitizeSubject"/> and assert the result
/// validates clean. Contextual rules (file names) are intentionally NOT closed
/// here — those are relaxed by the hook's driver tier.
/// </summary>
public sealed class CommitMessageSanitizerHardeningTests
{
    private static readonly CommitLintContext Structural = CommitLintContext.Driver([], []);

    private static void AssertValidatesClean(string message)
    {
        var violations = CommitMessageValidator.Validate(message, Structural);
        Assert.True(violations.Count == 0,
            "expected sanitized output to validate clean, but got: "
            + string.Join("; ", violations.Select(v => v.Message)) + $"\n--- message ---\n{message}");
    }

    [Fact]
    public void UppercaseAfterPrefix_LowercasedSoItValidates()
    {
        var subject = CommitMessageSanitizer.TrySanitizeSubject("feat: Add a control");
        Assert.NotNull(subject);
        AssertValidatesClean(subject!);
    }

    [Fact]
    public void EmDashSubject_ConvertedSoItValidates()
    {
        var subject = CommitMessageSanitizer.TrySanitizeSubject("feat: add a control — the new one");
        Assert.NotNull(subject);
        AssertValidatesClean(subject!);
    }

    [Fact]
    public void TrailingPeriod_StrippedSoItValidates()
    {
        var subject = CommitMessageSanitizer.TrySanitizeSubject("feat: add a control.");
        Assert.NotNull(subject);
        AssertValidatesClean(subject!);
    }

    [Fact]
    public void BreakingChangePrefix_Accepted()
    {
        var subject = CommitMessageSanitizer.TrySanitizeSubject("feat!: drop the legacy launcher");
        Assert.NotNull(subject);
        AssertValidatesClean(subject!);
    }

    [Fact]
    public void BadScope_RejectedToFallback()
    {
        // An uppercase scope is not a valid structural prefix, so the subject
        // path must reject it (returns null) rather than emit something the
        // validator would flag.
        var subject = CommitMessageSanitizer.TrySanitizeSubject("feat(App): add a control");
        Assert.Null(subject);
    }

    [Fact]
    public void MoreThanThreeBullets_CappedSoItValidates()
    {
        const string raw = """
            feat: add a control

            - one
            - two
            - three
            - four
            - five
            """;
        var message = CommitMessageSanitizer.FromRawOrFallback(raw, "my-task");
        AssertValidatesClean(message);
    }

    [Fact]
    public void OverLongBullet_TrimmedOnWordBoundarySoItValidates()
    {
        var longBullet = "- " + string.Join(' ', Enumerable.Range(1, 40).Select(i => $"word{i}"));
        var raw = $"feat: add a control\n\n{longBullet}";
        var message = CommitMessageSanitizer.FromRawOrFallback(raw, "my-task");
        AssertValidatesClean(message);
        // The trim must land on a word boundary — no partial 'word' fragment.
        Assert.DoesNotContain("word40", message, StringComparison.Ordinal);
        Assert.Contains("word1", message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmDashInBullet_ConvertedSoItValidates()
    {
        const string raw = "feat: add a control\n\n- the new control — wired to the lock";
        var message = CommitMessageSanitizer.FromRawOrFallback(raw, "my-task");
        AssertValidatesClean(message);
    }

    [Fact]
    public void NoPrefix_FallsBackAndValidates()
    {
        var message = CommitMessageSanitizer.FromRawOrFallback("just some words, no prefix", "my-task");
        AssertValidatesClean(message);
    }

    [Fact]
    public void NullRaw_FallbackValidates()
    {
        AssertValidatesClean(CommitMessageSanitizer.FromRawOrFallback(null, "enforce-conventional-commits-csharp"));
    }

    public static IEnumerable<object[]> OverflowInternalPeriodCases()
    {
        // Each case overflows 72 chars and is built so the word-boundary cut
        // leaves a surviving word ending in an internal period — the exact shape
        // that produced a trailing '.' before the post-truncate re-strip. These
        // heads all keep a non-empty description after the cut; the degenerate
        // collapse-to-prefix case is covered by the fallback test below.
        foreach (var head in new[] { 60, 62, 63, 64 })
            yield return [$"feat: {new string('a', head)}. tail goes here and is long enough to overflow the limit"];
    }

    [Theory]
    [MemberData(nameof(OverflowInternalPeriodCases))]
    public void OverflowWithInternalPeriod_DoesNotEndWithPeriod(string raw)
    {
        Assert.True(raw.Length > CommitRules.MaxSubjectChars, "test input must overflow to exercise truncation");
        var subject = CommitMessageSanitizer.TrySanitizeSubject(raw);
        Assert.NotNull(subject);
        Assert.False(subject!.EndsWith('.'), $"sanitized subject must not end with a period: \"{subject}\"");
        AssertValidatesClean(subject);
    }

    [Fact]
    public void OverflowStrippedToEmptyDescription_FallsBackAndValidates()
    {
        // After the word-boundary cut and trailing-period strip, the description
        // can be emptied; the sanitizer must take its fallback rather than emit
        // "feat: " with nothing after the prefix.
        var raw = "feat: " + new string('a', 80) + ". tail";
        var message = CommitMessageSanitizer.FromRawOrFallback(raw, "my-task");
        AssertValidatesClean(message);
    }

    [Fact]
    public void TabSeparatedBullet_OverTwentyWords_TrimmedSoItValidates()
    {
        // The validator counts words on any whitespace, so a tab-separated bullet
        // over the cap must be trimmed by the sanitizer to match — not undercounted.
        var bulletWords = string.Join('\t', Enumerable.Range(1, 25).Select(i => $"word{i}"));
        var raw = $"feat: add a control\n\n- {bulletWords}";
        var message = CommitMessageSanitizer.FromRawOrFallback(raw, "my-task");
        AssertValidatesClean(message);
    }

    [Fact]
    public void FullMessyMessage_FullyValidates()
    {
        const string raw = """
            FEAT: Rework the Whole Thing.

            - this first bullet has way too many words that should be trimmed back down to twenty so the validator does not complain at all about it
            - the second bullet — has an em dash
            - third
            - fourth bullet over the cap
            """;
        // The subject "FEAT:" is not a valid lowercase type, so this falls back.
        var message = CommitMessageSanitizer.FromRawOrFallback(raw, "my-task");
        AssertValidatesClean(message);
    }
}
