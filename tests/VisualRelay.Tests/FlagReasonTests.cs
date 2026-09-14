using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;

namespace VisualRelay.Tests;

/// <summary>
/// A flag reason is one line. Measured on the Windows arm with apache/commons-lang: a test run
/// reaped at Author-tests flagged with the whole 80 KB Maven log as its reason, so NEEDS-REVIEW
/// was 81 KB, run.log's flagged line and status.json's error carried it all, and the drain log's
/// "flagged" milestone was a single 80 KB line.
/// </summary>
public sealed class FlagReasonTests
{
    private static readonly string MavenLog =
        string.Concat(Enumerable.Repeat("[INFO] Running org.apache.commons.lang3.SomeTest\n", 2_000));

    [Fact]
    public void AReasonCarryingATestLog_KeepsItsFirstLine_AndTheLogMovesToTheDetails()
    {
        var (reason, details) = FlagReason.Split(
            "test command timed out: the process tree went output-silent and CPU-idle\n\n" + MavenLog, details: null);

        Assert.Equal("test command timed out: the process tree went output-silent and CPU-idle", reason);
        Assert.StartsWith("[INFO] Running", details, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMovedText_ComesBeforeTheDetailsTheCallerAlreadyHad()
    {
        var (_, details) = FlagReason.Split("verify failed\nfirst failure", details: "setup checks");

        Assert.Equal("first failure\n\nsetup checks", details);
    }

    [Fact]
    public void AnOverlongSingleLine_IsCut_AndItsRestKept()
    {
        var line = new string('x', 2_000);

        var (reason, details) = FlagReason.Split(line, details: null);

        Assert.True(reason.Length <= FlagReason.MaxChars + 1, $"reason kept {reason.Length} characters");
        Assert.EndsWith("…", reason, StringComparison.Ordinal);
        Assert.Equal(line.Length - FlagReason.MaxChars, details!.Length);
    }

    /// <summary>
    /// The hint Visual Relay appends to a raw error is guidance for whoever reviews the task, so it
    /// stays in the reason even when a whole test log sits between it and the first line.
    /// </summary>
    [Fact]
    public void AHintTheReasonEndsWith_StaysInTheReason()
    {
        var (reason, details) = FlagReason.Split(
            "test command timed out after 300000ms\n" + MavenLog + "\n\nHint: re-run a targeted subset.", details: null);

        Assert.Equal("test command timed out after 300000ms Hint: re-run a targeted subset.", reason);
        Assert.DoesNotContain("Hint:", details, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortReason_IsLeftAlone()
    {
        Assert.Equal(("stage cancelled", (string?)"why"), FlagReason.Split("stage cancelled", details: "why"));
    }

    [Fact]
    public void TheDrainLog_WritesOneLinePerMilestone_WhateverTheDetail()
    {
        using var repo = TestRepository.Create();

        DrainSummaryLog.Write(repo.Root, "drain-1", "alpha", "execute", "flagged", "timed out\n\n" + MavenLog);

        var lines = File.ReadAllLines(Path.Combine(repo.Root, ".relay", "drain-1.log"));
        var line = Assert.Single(lines);
        Assert.Contains("(timed out)", line, StringComparison.Ordinal);
        Assert.True(line.Length < 1_000, $"the milestone line is {line.Length} characters");
    }
}
