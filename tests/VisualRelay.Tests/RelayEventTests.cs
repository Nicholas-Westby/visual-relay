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
}
