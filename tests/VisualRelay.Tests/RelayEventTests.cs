using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayEventTests
{
    private static RelayEvent EventWithLevel(string level) =>
        new(DateTimeOffset.UtcNow, level, "stage_failed", "run-1", "/root");

    [Theory]
    [InlineData("warn")]
    [InlineData("error")]
    public void IsAttention_True_ForWarnAndError(string level)
    {
        Assert.True(EventWithLevel(level).IsAttention);
    }

    [Fact]
    public void IsAttention_False_ForInfo()
    {
        Assert.False(EventWithLevel("info").IsAttention);
    }

    [Fact]
    public void StageEscalated_RendersLabeledRunLogEntry()
    {
        // The Run Log (RunLogView) binds DisplayLine + DetailLine. A stage_escalated
        // event must surface the full labeled transition so each escalation is
        // clearly visible in the Run Log tab, and read as attention (warn).
        var escalation = new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "stage_escalated", "run-1", "/root", "task", 10, "frontier",
            Data: new Dictionary<string, string>
            {
                ["message"] = StageEscalation.DescribeTransition(10, "Fix-verify", 2, 3, "balanced", "frontier", 200, 400)
            });

        Assert.Equal("s10/frontier stage_escalated", escalation.DisplayLine);
        Assert.Contains(
            "Stage 10 Fix-verify escalated (run 2/3): tier balanced→frontier, max-turns 200→400",
            escalation.DetailLine, StringComparison.Ordinal);
        Assert.True(escalation.IsAttention);
    }
}
