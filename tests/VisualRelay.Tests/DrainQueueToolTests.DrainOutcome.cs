using VisualRelay.Domain;
using VisualRelay.DrainQueue;

namespace VisualRelay.Tests;

// DrainOutcome tests, split out of DrainQueueToolTests.cs to keep each file
// under the 300-line guard.
public sealed partial class DrainQueueToolTests
{
    // ═══════════════════════════════════════════════════════════════
    // DrainOutcome.GetExitCode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetExitCode_AllCommitted_ReturnsZero()
    {
        var outcomes = new RelayTaskOutcome[]
        {
            new("alpha", RelayTaskOutcomeStatus.Committed, "h1", "abc123", null),
            new("beta", RelayTaskOutcomeStatus.Committed, "h2", "def456", null),
        };

        var code = DrainOutcome.GetExitCode(outcomes);
        Assert.Equal(0, code);
    }

    [Fact]
    public void GetExitCode_AnyFlagged_ReturnsTwo()
    {
        var outcomes = new RelayTaskOutcome[]
        {
            new("alpha", RelayTaskOutcomeStatus.Committed, "h1", "abc123", null),
            new("beta", RelayTaskOutcomeStatus.Flagged, null, null, "author-tests not red"),
        };

        var code = DrainOutcome.GetExitCode(outcomes);
        Assert.Equal(2, code);
    }

    [Fact]
    public void GetExitCode_AnyFailed_ReturnsTwo()
    {
        var outcomes = new RelayTaskOutcome[]
        {
            new("alpha", RelayTaskOutcomeStatus.Committed, "h1", "abc123", null),
            new("beta", RelayTaskOutcomeStatus.Failed, null, null, "test timeout"),
        };

        var code = DrainOutcome.GetExitCode(outcomes);
        Assert.Equal(2, code);
    }

    [Fact]
    public void GetExitCode_EmptyList_ReturnsZero()
    {
        var code = DrainOutcome.GetExitCode(Array.Empty<RelayTaskOutcome>());
        Assert.Equal(0, code);
    }

    [Fact]
    public void GetExitCode_OnlyPlanned_ReturnsZero()
    {
        // A drain that was paused after Phase 1 — only Planned outcomes.
        var outcomes = new RelayTaskOutcome[]
        {
            new("alpha", RelayTaskOutcomeStatus.Planned, null, null, null),
            new("beta", RelayTaskOutcomeStatus.Planned, null, null, null),
        };

        var code = DrainOutcome.GetExitCode(outcomes);
        Assert.Equal(0, code);
    }

    [Fact]
    public void GetExitCode_MixedPlannedAndFlagged_ReturnsTwo()
    {
        var outcomes = new RelayTaskOutcome[]
        {
            new("alpha", RelayTaskOutcomeStatus.Planned, null, null, null),
            new("beta", RelayTaskOutcomeStatus.Flagged, null, null, "stage 3 flag"),
        };

        var code = DrainOutcome.GetExitCode(outcomes);
        Assert.Equal(2, code);
    }

    // ═══════════════════════════════════════════════════════════════
    // DrainOutcome.FormatOutcomeLine
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatOutcomeLine_Committed_UsesSha()
    {
        var outcome = new RelayTaskOutcome("my-task", RelayTaskOutcomeStatus.Committed,
            "taskhash", "abc123def", null);

        var line = DrainOutcome.FormatOutcomeLine(outcome);
        Assert.Contains("Committed", line, StringComparison.Ordinal);
        Assert.Contains("my-task", line, StringComparison.Ordinal);
        Assert.Contains("abc123def", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOutcomeLine_Flagged_UsesReason()
    {
        var outcome = new RelayTaskOutcome("bad-task", RelayTaskOutcomeStatus.Flagged,
            null, null, "stage 5 gate did not pass");

        var line = DrainOutcome.FormatOutcomeLine(outcome);
        Assert.Contains("Flagged", line, StringComparison.Ordinal);
        Assert.Contains("bad-task", line, StringComparison.Ordinal);
        Assert.Contains("stage 5 gate did not pass", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOutcomeLine_Failed_UsesReason()
    {
        var outcome = new RelayTaskOutcome("crash-task", RelayTaskOutcomeStatus.Failed,
            null, null, "test timeout after 300s");

        var line = DrainOutcome.FormatOutcomeLine(outcome);
        Assert.Contains("Failed", line, StringComparison.Ordinal);
        Assert.Contains("crash-task", line, StringComparison.Ordinal);
        Assert.Contains("test timeout after 300s", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOutcomeLine_Planned_ShowsPlanned()
    {
        var outcome = new RelayTaskOutcome("paused-task", RelayTaskOutcomeStatus.Planned,
            null, null, null);

        var line = DrainOutcome.FormatOutcomeLine(outcome);
        Assert.Contains("Planned", line, StringComparison.Ordinal);
        Assert.Contains("paused-task", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOutcomeLine_MatchesRunTaskFormat()
    {
        // Must match the RunTask format: "Status: taskId sha-or-reason"
        var outcome = new RelayTaskOutcome("t1", RelayTaskOutcomeStatus.Committed,
            "hash", "abc123", null);

        var line = DrainOutcome.FormatOutcomeLine(outcome);

        // The format should be: "Status: taskId sha-or-reason"
        Assert.StartsWith("Committed: t1", line, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // DrainOutcome.ComputeSummary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ComputeSummary_CountsEachStatus()
    {
        var outcomes = new RelayTaskOutcome[]
        {
            new("a", RelayTaskOutcomeStatus.Committed, "h", "sha1", null),
            new("b", RelayTaskOutcomeStatus.Committed, "h", "sha2", null),
            new("c", RelayTaskOutcomeStatus.Flagged, null, null, "reason1"),
            new("d", RelayTaskOutcomeStatus.Failed, null, null, "reason2"),
            new("e", RelayTaskOutcomeStatus.Planned, null, null, null),
        };

        var summary = DrainOutcome.ComputeSummary(outcomes);

        Assert.Equal(2, summary.Committed);
        Assert.Equal(1, summary.Flagged);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Planned);
    }

    [Fact]
    public void ComputeSummary_EmptyList_AllZeros()
    {
        var summary = DrainOutcome.ComputeSummary(Array.Empty<RelayTaskOutcome>());
        Assert.Equal(0, summary.Committed);
        Assert.Equal(0, summary.Flagged);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Planned);
    }

    // ═══════════════════════════════════════════════════════════════
    // DrainOutcome.FormatSummary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatSummary_FormatsAllCounts()
    {
        var summary = new DrainOutcome.Summary(Committed: 3, Flagged: 1, Failed: 0, Planned: 2);
        var line = DrainOutcome.FormatSummary(summary);

        Assert.Contains("Committed: 3", line, StringComparison.Ordinal);
        Assert.Contains("Flagged: 1", line, StringComparison.Ordinal);
        Assert.Contains("Failed: 0", line, StringComparison.Ordinal);
        Assert.Contains("Planned: 2", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSummary_AllZeros()
    {
        var summary = new DrainOutcome.Summary(Committed: 0, Flagged: 0, Failed: 0, Planned: 0);
        var line = DrainOutcome.FormatSummary(summary);

        Assert.Contains("Committed: 0", line, StringComparison.Ordinal);
        Assert.Contains("Flagged: 0", line, StringComparison.Ordinal);
        Assert.Contains("Failed: 0", line, StringComparison.Ordinal);
        Assert.Contains("Planned: 0", line, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // DrainOutcome.NothingPendingMessage
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NothingPendingMessage_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(DrainOutcome.NothingPendingMessage));
    }

    [Fact]
    public void NothingPendingMessage_ContainsQueueOrEmptyHint()
    {
        Assert.Contains("empty", DrainOutcome.NothingPendingMessage, StringComparison.OrdinalIgnoreCase);
    }
}
