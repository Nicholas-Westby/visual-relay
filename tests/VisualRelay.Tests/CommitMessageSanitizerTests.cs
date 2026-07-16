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

    [Fact]
    public void FromRawOrFallback_VeryLongTaskId_KeepsTruncatedIdNeverEmpty()
    {
        // Regression: the real JobFinder task id (~78 chars). Because the only space
        // in "chore(relay): <id>" sits right after the colon, the old word-boundary
        // truncate dropped the entire id and produced a bare "chore(relay):".
        const string taskId = "fix-job-sb-energy-member-of-technical-staff-agent-workflow-systems-and-evaluat";
        var result = CommitMessageSanitizer.FromRawOrFallback(null, taskId);

        Assert.StartsWith("chore(relay): ", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 72, $"subject exceeded 72 chars: {result.Length}");
        // The description must not be empty.
        Assert.NotEqual("chore(relay):", result.TrimEnd());
        Assert.NotEqual("chore(relay): ", result);
        // A recognizable head of the id must survive.
        Assert.Contains("fix-job-sb-energy", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FromRawOrFallback_ShortTaskId_SubjectUnchanged()
    {
        var result = CommitMessageSanitizer.FromRawOrFallback(null, "ocr-failures");
        Assert.Equal("chore(relay): ocr-failures", result);
    }

    [Fact]
    public void FromRawOrFallback_TaskIdExactlyAtLimit_KeptWhole()
    {
        // "chore(relay): " is 14 chars; pad the id so the full subject is exactly 72.
        var taskId = new string('a', 72 - "chore(relay): ".Length);
        var result = CommitMessageSanitizer.FromRawOrFallback(null, taskId);

        Assert.Equal($"chore(relay): {taskId}", result);
        Assert.Equal(72, result.Length);
    }

    [Fact]
    public void FromRawOrFallback_TaskIdOneOverLimit_TruncatedWithinLimit()
    {
        var taskId = new string('a', 72 - "chore(relay): ".Length + 1);
        var result = CommitMessageSanitizer.FromRawOrFallback(null, taskId);

        Assert.StartsWith("chore(relay): ", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 72, $"subject exceeded 72 chars: {result.Length}");
        Assert.NotEqual("chore(relay):", result.TrimEnd());
        Assert.Contains("aaaa", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySanitizeMessage_KeepsSanitizedBodyBullets()
    {
        var raw = "perf: Speed up retry tests.\n"
                  + "- test time dropped from 4s to 1.4s — file total\n"
                  + "prose lines are not bullets and are dropped\n"
                  + "- second bullet";

        var result = CommitMessageSanitizer.TrySanitizeMessage(raw);

        Assert.Equal(
            "perf: speed up retry tests"
            + Environment.NewLine + Environment.NewLine
            + "- test time dropped from 4s to 1.4s - file total"
            + Environment.NewLine
            + "- second bullet",
            result);
    }

    [Fact]
    public void TrySanitizeMessage_SubjectOnly_ReturnsBareSubject()
    {
        var result = CommitMessageSanitizer.TrySanitizeMessage("fix: patch bug");
        Assert.Equal("fix: patch bug", result);
    }

    [Fact]
    public void TrySanitizeMessage_NonConventionalSubject_ReturnsNull()
    {
        var result = CommitMessageSanitizer.TrySanitizeMessage("update stuff\n\n- with a bullet");
        Assert.Null(result);
    }

    [Fact]
    public void TrySanitizeMessage_CapsBulletsAtThree_AndWordsAtTwenty()
    {
        var manyWords = string.Join(' ', Enumerable.Range(1, 25).Select(i => $"w{i}"));
        var raw = $"feat: add thing\n- {manyWords}\n- two\n- three\n- four";

        var result = CommitMessageSanitizer.TrySanitizeMessage(raw);

        Assert.NotNull(result);
        var lines = result!.Split(Environment.NewLine);
        // subject + blank + 3 bullets, the fourth dropped
        Assert.Equal(5, lines.Length);
        // "- " prefix plus exactly 20 words survives the cap.
        Assert.Equal("- " + string.Join(' ', Enumerable.Range(1, 20).Select(i => $"w{i}")), lines[2]);
        Assert.Equal("- two", lines[3]);
        Assert.Equal("- three", lines[4]);
    }
}
