using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ElapsedFormatterTests
{
    [Fact]
    public void Label_ZeroSeconds_Returns0s()
    {
        Assert.Equal("0s", ElapsedFormatter.Label(TimeSpan.Zero));
    }

    [Fact]
    public void Label_SingleDigitSeconds_ReturnsNs()
    {
        Assert.Equal("7s", ElapsedFormatter.Label(TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void Label_UnderOneMinute_ReturnsSecondsOnly()
    {
        Assert.Equal("59s", ElapsedFormatter.Label(TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void Label_ExactlyOneMinute_Returns1m00s()
    {
        Assert.Equal("1m 00s", ElapsedFormatter.Label(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Label_MinutesAndSeconds_ReturnsMmSs()
    {
        Assert.Equal("1m 04s", ElapsedFormatter.Label(TimeSpan.FromSeconds(64)));
    }

    [Fact]
    public void Label_TwoMinutes36Seconds_Returns2m36s()
    {
        Assert.Equal("2m 36s", ElapsedFormatter.Label(TimeSpan.FromSeconds(156)));
    }

    [Fact]
    public void Label_TenMinutesExactly_Returns10m00s()
    {
        Assert.Equal("10m 00s", ElapsedFormatter.Label(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Label_SubSecond_Positive_RoundsTo0s()
    {
        Assert.Equal("0s", ElapsedFormatter.Label(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Label_Negative_Returns0s()
    {
        Assert.Equal("0s", ElapsedFormatter.Label(TimeSpan.FromSeconds(-5)));
    }
}
