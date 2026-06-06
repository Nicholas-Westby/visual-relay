using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class CommitMessageSanitizerTests
{
    [Fact]
    public void TrySanitizeSubject_ValidConventionalSubject_ReturnsSubject()
    {
        var result = CommitMessageSanitizer.TrySanitizeSubject("feat: add queue controls");
        Assert.Equal("feat: add queue controls", result);
    }

    [Fact]
    public void TrySanitizeSubject_ValidScopedSubject_ReturnsSubject()
    {
        var result = CommitMessageSanitizer.TrySanitizeSubject("fix(core): release active lock");
        Assert.Equal("fix(core): release active lock", result);
    }

    [Fact]
    public void TrySanitizeSubject_NonConventionalSubject_ReturnsNull()
    {
        var result = CommitMessageSanitizer.TrySanitizeSubject("update the thing");
        Assert.Null(result);
    }

    [Fact]
    public void TrySanitizeSubject_NullInput_ReturnsNull()
    {
        var result = CommitMessageSanitizer.TrySanitizeSubject(null);
        Assert.Null(result);
    }

    [Fact]
    public void TrySanitizeSubject_WhitespaceInput_ReturnsNull()
    {
        var result = CommitMessageSanitizer.TrySanitizeSubject("   ");
        Assert.Null(result);
    }

    [Fact]
    public void TrySanitizeSubject_TrailingPeriodStripped()
    {
        var result = CommitMessageSanitizer.TrySanitizeSubject("feat: add something.");
        Assert.Equal("feat: add something", result);
    }

    [Fact]
    public void TrySanitizeSubject_TruncatesAt72Chars()
    {
        // The truncate helper cuts at the last space to avoid breaking words.
        // Make sure the space is after the Conventional prefix so it survives.
        var longSubject = "feat: add " + new string('x', 200);
        var result = CommitMessageSanitizer.TrySanitizeSubject(longSubject);
        Assert.NotNull(result);
        Assert.True(result!.Length <= 72);
    }

    [Fact]
    public void FromRawOrFallback_WithValidInput_ReturnsSubject()
    {
        var result = CommitMessageSanitizer.FromRawOrFallback("fix: patch bug", "my-task");
        Assert.Equal("fix: patch bug", result);
    }

    [Fact]
    public void FromRawOrFallback_WithNullInput_ReturnsSlugFallback()
    {
        var result = CommitMessageSanitizer.FromRawOrFallback(null, "my-task");
        Assert.Equal("chore(relay): my-task", result);
    }
}
