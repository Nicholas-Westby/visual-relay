using VisualRelay.Core.CommitLint;

namespace VisualRelay.Tests;

/// <summary>
/// Structural-tier rules: enforced for every commit regardless of who authors
/// it. Each test isolates one rule and asserts it is flagged exactly once with
/// wording that mirrors ai-sorcery's <c>check-commit-message.ts</c>.
/// </summary>
public sealed class CommitMessageValidatorStructuralTests
{
    private static readonly CommitLintContext Human = CommitLintContext.Human(
        changedBasenames: [], disallowedSubstrings: []);

    private static IReadOnlyList<Violation> Validate(string message) =>
        CommitMessageValidator.Validate(message, Human);

    [Fact]
    public void FullyValidMessage_NoViolations()
    {
        const string message = """
            feat(core): add queue controls

            - introduce a pause action on the active lock
            - surface the queued task count in the status bar
            """;
        Assert.Empty(Validate(message));
    }

    [Fact]
    public void BreakingChangeBang_Passes()
    {
        Assert.Empty(Validate("feat!: drop the legacy launcher path"));
    }

    [Fact]
    public void MissingConventionalPrefix_Flagged()
    {
        var violations = Validate("update the thing");
        Assert.Single(violations);
        Assert.Contains(violations, v => v.Message.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownType_Flagged()
    {
        var violations = Validate("wip: half a thing");
        Assert.Contains(violations, v => v.Message.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UppercaseScope_Flagged()
    {
        var violations = Validate("feat(App): add a control");
        Assert.Contains(violations, v => v.Message.Contains("scope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LowercaseScope_Passes()
    {
        Assert.Empty(Validate("fix(core-2): release the active lock"));
    }

    [Fact]
    public void UppercaseAfterPrefix_Flagged()
    {
        var violations = Validate("feat: Add a control");
        Assert.Contains(violations, v => v.Message.Contains("lowercase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SubjectOver72Chars_Flagged()
    {
        var subject = "feat: " + new string('x', 80);
        var violations = Validate(subject);
        Assert.Contains(violations, v => v.Message.Contains("72", StringComparison.Ordinal));
    }

    [Fact]
    public void SubjectExactly72Chars_Passes()
    {
        var subject = "feat: " + new string('x', 72 - "feat: ".Length);
        Assert.Equal(72, subject.Length);
        Assert.Empty(Validate(subject));
    }

    [Fact]
    public void SubjectTrailingPeriod_Flagged()
    {
        var violations = Validate("feat: add a control.");
        Assert.Contains(violations, v => v.Message.Contains("period", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmDashInSubject_Flagged()
    {
        var violations = Validate("feat: add a control — the new one");
        Assert.Contains(violations, v => v.Message.Contains("em dash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmDashInBody_Flagged()
    {
        const string message = """
            feat: add a control

            - the new control — wired to the lock
            """;
        var violations = Validate(message.Replace("\\u2014", "—", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Message.Contains("em dash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingBlankLineBeforeBody_Flagged()
    {
        const string message = "feat: add a control\n- a bullet right after the subject";
        var violations = Validate(message);
        Assert.Contains(violations, v => v.Message.Contains("blank line", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProseBodyLine_Flagged()
    {
        const string message = """
            feat: add a control

            this is prose, not a bullet
            """;
        var violations = Validate(message);
        Assert.Contains(violations, v => v.Message.Contains("bullet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MoreThanThreeBullets_Flagged()
    {
        const string message = """
            feat: add a control

            - one
            - two
            - three
            - four
            """;
        var violations = Validate(message);
        Assert.Contains(violations, v => v.Message.Contains("3", StringComparison.Ordinal));
    }

    [Fact]
    public void BulletOverTwentyWords_Flagged()
    {
        var longBullet = "- " + string.Join(' ', Enumerable.Range(1, 21).Select(i => $"w{i}"));
        var message = $"feat: add a control\n\n{longBullet}";
        var violations = Validate(message);
        Assert.Contains(violations, v => v.Message.Contains("20", StringComparison.Ordinal));
    }

    [Fact]
    public void BulletExactlyTwentyWords_Passes()
    {
        var bullet = "- " + string.Join(' ', Enumerable.Range(1, 20).Select(i => $"w{i}"));
        var message = $"feat: add a control\n\n{bullet}";
        Assert.Empty(Validate(message));
    }

    [Fact]
    public void CommentLinesStripped_BeforeChecks()
    {
        // A '#'-prefixed comment line that, if counted, would be a prose body
        // line. git drops it; so must we.
        const string message = """
            feat: add a control

            - the only real bullet
            # this comment line must be ignored
            """;
        Assert.Empty(Validate(message));
    }

    [Fact]
    public void TrailerBlockExempt_FromEmDashAndProse()
    {
        // Trailers are stripped before checks: an em dash inside a trailer and
        // the trailer lines themselves must not be flagged.
        const string message = """
            feat: add a control

            - the only real bullet
            Co-Authored-By: Someone — Person <a@b.test>
            Task: enforce-conventional-commits-csharp
            Relay-Seal: deadbeef
            """;
        var withEmDash = message.Replace("\\u2014", "—", StringComparison.Ordinal);
        Assert.Empty(Validate(withEmDash));
    }
}
