using VisualRelay.Core.CommitLint;

namespace VisualRelay.Tests;

/// <summary>
/// Contextual-tier rules: changed-file basenames, path-like tokens, and the
/// optional disallowed-substring blocklist. These are enforced for human/dev
/// commits but skipped for the driver's in-run sealed commit (the
/// <see cref="CommitLintContext.Driver"/> tier), because Visual Relay's real
/// commits reference file names constantly and we never lossily scrub them.
/// </summary>
public sealed class CommitMessageValidatorContextualTests
{
    [Fact]
    public void ChangedFileBasenameInSubject_FlaggedForHuman()
    {
        var ctx = CommitLintContext.Human(["GitInvoker.cs"], []);
        var violations = CommitMessageValidator.Validate("fix: patch GitInvoker.cs resolution", ctx);
        Assert.Contains(violations, v => v.Message.Contains("changed file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangedFileBasenameInBullet_FlaggedForHuman()
    {
        var ctx = CommitLintContext.Human(["GitInvoker.cs"], []);
        const string message = """
            fix: patch the resolver

            - tweak GitInvoker.cs to pin the binary
            """;
        var violations = CommitMessageValidator.Validate(message, ctx);
        Assert.Contains(violations, v => v.Message.Contains("changed file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangedFileBasenameInBullet_PassesForDriver()
    {
        // The same message a human would be rejected for is accepted for the
        // driver's sealed commit (contextual tier relaxed).
        var ctx = CommitLintContext.Driver(["GitInvoker.cs"], []);
        const string message = """
            fix: patch the resolver

            - tweak GitInvoker.cs to pin the binary
            """;
        Assert.Empty(CommitMessageValidator.Validate(message, ctx));
    }

    [Fact]
    public void ShortBareBasenameWithoutDot_NotFlagged()
    {
        // The reference only matches basenames that contain '.' OR are >= 6
        // chars. A short, dotless name like "app" must not be matched.
        var ctx = CommitLintContext.Human(["app"], []);
        Assert.Empty(CommitMessageValidator.Validate("feat: add an app launcher control", ctx));
    }

    [Fact]
    public void LongBareBasenameWithoutDot_Flagged()
    {
        // A bare basename >= 6 chars (no dot) is matched.
        var ctx = CommitLintContext.Human(["Makefile"], []);
        var violations = CommitMessageValidator.Validate("chore: refresh the Makefile recipe", ctx);
        Assert.Contains(violations, v => v.Message.Contains("changed file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathLikeTokenInSubject_FlaggedForHuman()
    {
        var ctx = CommitLintContext.Human([], []);
        var violations = CommitMessageValidator.Validate("fix: correct src/core/lock logic", ctx);
        Assert.Contains(violations, v => v.Message.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathLikeTokenInBullet_FlaggedForHuman()
    {
        var ctx = CommitLintContext.Human([], []);
        const string message = """
            fix: correct the lock logic

            - rework the path under src/core/lock for clarity
            """;
        var violations = CommitMessageValidator.Validate(message, ctx);
        Assert.Contains(violations, v => v.Message.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathLikeToken_PassesForDriver()
    {
        var ctx = CommitLintContext.Driver([], []);
        Assert.Empty(CommitMessageValidator.Validate("fix: correct src/core/lock logic", ctx));
    }

    [Fact]
    public void SlashWithSpaceAround_NotAPathToken()
    {
        // "and / or" — a slash with whitespace on a side is not a path token.
        var ctx = CommitLintContext.Human([], []);
        Assert.Empty(CommitMessageValidator.Validate("chore: tidy the and / or handling", ctx));
    }

    [Fact]
    public void DisallowedSubstring_FlaggedCaseInsensitive_ForHuman()
    {
        var ctx = CommitLintContext.Human([], ["wip"]);
        var violations = CommitMessageValidator.Validate("feat: a WIP control panel", ctx);
        Assert.Contains(violations, v => v.Message.Contains("disallowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisallowedSubstring_SkippedForDriver()
    {
        var ctx = CommitLintContext.Driver([], ["wip"]);
        Assert.Empty(CommitMessageValidator.Validate("feat: a WIP control panel", ctx));
    }

    [Fact]
    public void StructuralViolation_StillFlaggedForDriver()
    {
        // The driver tier only relaxes contextual rules; structural rules still
        // apply (here: trailing period).
        var ctx = CommitLintContext.Driver([], []);
        var violations = CommitMessageValidator.Validate("feat: add a control.", ctx);
        Assert.Contains(violations, v => v.Message.Contains("period", StringComparison.OrdinalIgnoreCase));
    }
}
