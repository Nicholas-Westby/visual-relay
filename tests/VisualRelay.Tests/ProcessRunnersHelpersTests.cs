using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class ProcessRunnersHelpersTests
{
    [Fact]
    public void FormatCeilingMs_ExactMinutes()
    {
        var result = SwivalSubagentRunner.FormatCeilingMs(1_800_000);
        Assert.Equal("30m 00s (1800000 ms)", result);
    }

    [Fact]
    public void FormatCeilingMs_WithSeconds()
    {
        var result = SwivalSubagentRunner.FormatCeilingMs(1_830_000);
        Assert.Equal("30m 30s (1830000 ms)", result);
    }

    [Fact]
    public void FormatCeilingMs_Zero()
    {
        var result = SwivalSubagentRunner.FormatCeilingMs(0);
        Assert.Equal("0m 00s (0 ms)", result);
    }
}
