using VisualRelay.Core.Authorship;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeTrailerStripper.Strip"/> — the pure
/// trailer-block scrubber that removes any commit-message trailer whose key
/// or value mentions "Claude" (case-insensitive), while leaving the subject,
/// body prose, and non-Claude trailers byte-identical.
/// </summary>
public sealed class ClaudeTrailerStripperTests
{
    [Fact]
    public void Strip_RealWorldClaudeTrailers_BothRemoved()
    {
        var message =
            "feat: add widget\n" +
            "\n" +
            "Some body text explaining the change.\n" +
            "\n" +
            "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>\n" +
            "Claude-Session: https://claude.ai/code/session_01G8FrQ7mD2TzsgXbofvnu74\n";

        var result = ClaudeTrailerStripper.Strip(message);

        Assert.DoesNotContain("Claude", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anthropic", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feat: add widget", result, StringComparison.Ordinal);
        Assert.Contains("Some body text explaining the change.", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_ClaudeSessionAndLowercaseCoAuthor_RemovedCaseInsensitively()
    {
        var message =
            "fix(core): tighten lock\n" +
            "\n" +
            "Reviewed-by: Jane <jane@example.com>\n" +
            "co-authored-by: Claude Sonnet <noreply@anthropic.com>\n" +
            "Claude-Session: https://claude.ai/code/abc\n";

        var result = ClaudeTrailerStripper.Strip(message);

        Assert.DoesNotContain("Claude", result, StringComparison.OrdinalIgnoreCase);
        // Non-Claude trailer survives.
        Assert.Contains("Reviewed-by: Jane <jane@example.com>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_HumanCoAuthor_Kept()
    {
        var message =
            "feat: ship it\n" +
            "\n" +
            "Co-Authored-By: Jane Doe <jane@example.com>\n";

        var result = ClaudeTrailerStripper.Strip(message);

        Assert.Contains("Co-Authored-By: Jane Doe <jane@example.com>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_MixedBlock_KeepsNonClaudeTrailersWithSpacingIntact()
    {
        var message =
            "feat: thing\n" +
            "\n" +
            "Signed-off-by: Dev One <dev1@example.com>\n" +
            "Co-Authored-By: Claude <noreply@anthropic.com>\n" +
            "Reviewed-by: Dev Two <dev2@example.com>\n" +
            "Claude-Session: https://claude.ai/code/xyz\n";

        var result = ClaudeTrailerStripper.Strip(message);

        var expected =
            "feat: thing\n" +
            "\n" +
            "Signed-off-by: Dev One <dev1@example.com>\n" +
            "Reviewed-by: Dev Two <dev2@example.com>\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Strip_FoldedMultiLineClaudeValue_RemovedWhole()
    {
        // A folded trailer: continuation lines begin with whitespace and belong
        // to the trailer above. The whole Claude trailer (key + folds) is dropped.
        var message =
            "feat: folded\n" +
            "\n" +
            "Signed-off-by: Dev <dev@example.com>\n" +
            "Claude-Note: line one mentioning claude\n" +
            "  continued line two\n" +
            "  continued line three\n";

        var result = ClaudeTrailerStripper.Strip(message);

        var expected =
            "feat: folded\n" +
            "\n" +
            "Signed-off-by: Dev <dev@example.com>\n";
        Assert.Equal(expected, result);
        Assert.DoesNotContain("continued line two", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_SubjectOnlyConventionalCommit_Unchanged()
    {
        // A single-line message has no trailer block; the colon-bearing subject
        // must never be treated as a trailer.
        var message = "feat(x): y\n";

        var result = ClaudeTrailerStripper.Strip(message);

        Assert.Equal(message, result);
    }

    [Fact]
    public void Strip_SubjectThenOnlyClaudeTrailer_LeavesSubjectAndTrailingNewline()
    {
        var message =
            "subject\n" +
            "\n" +
            "Claude-Session: https://claude.ai/code/abc\n";

        var result = ClaudeTrailerStripper.Strip(message);

        Assert.Equal("subject\n", result);
    }

    [Fact]
    public void Strip_BodyParagraphMentioningClaude_Kept()
    {
        // Prose in the body that mentions Claude is NOT a trailer and stays.
        var message =
            "feat: doc\n" +
            "\n" +
            "This change was discussed with Claude during design.\n" +
            "\n" +
            "Signed-off-by: Dev <dev@example.com>\n";

        var result = ClaudeTrailerStripper.Strip(message);

        Assert.Contains("This change was discussed with Claude during design.", result, StringComparison.Ordinal);
        Assert.Contains("Signed-off-by: Dev <dev@example.com>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_NonClaudeTrailerWithClaudeInValue_IntentionallyRemoved()
    {
        // Per the "any trailer mentioning Claude" rule, a value containing
        // "claude" causes removal even when the key is innocuous.
        var message =
            "feat: q\n" +
            "\n" +
            "Reviewed-by: claude-fan <x@y.example>\n" +
            "Signed-off-by: Dev <dev@example.com>\n";

        var result = ClaudeTrailerStripper.Strip(message);

        var expected =
            "feat: q\n" +
            "\n" +
            "Signed-off-by: Dev <dev@example.com>\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Strip_CleanedOutput_IsIdempotent()
    {
        var message =
            "feat: thing\n" +
            "\n" +
            "Signed-off-by: Dev One <dev1@example.com>\n" +
            "Co-Authored-By: Claude <noreply@anthropic.com>\n" +
            "Claude-Session: https://claude.ai/code/xyz\n";

        var once = ClaudeTrailerStripper.Strip(message);
        var twice = ClaudeTrailerStripper.Strip(once);

        Assert.Equal(once, twice);
    }
}
