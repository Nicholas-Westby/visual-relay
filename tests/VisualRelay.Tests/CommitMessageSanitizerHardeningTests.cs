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
