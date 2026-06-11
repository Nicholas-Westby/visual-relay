using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class ProcessTreeCpuSamplerTests
{
    [Theory]
    [InlineData("0:00.05", 50)]
    [InlineData("0:01.50", 1_500)]
    [InlineData("1:02.50", 62_500)]
    [InlineData("12:34.00", 754_000)]
    [InlineData("01:02:03", 3_723_000)]
    [InlineData("1:02:03", 3_723_000)]
    [InlineData("00:00:05", 5_000)]
    [InlineData("1-02:03:04", 93_784_000)]
    public void ParseCpuTimeMs_KnownFormats(string value, long expectedMs)
    {
        Assert.Equal(expectedMs, ProcessTreeCpuSampler.ParseCpuTimeMs(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("::")]
    [InlineData("1:２:3")]
    public void ParseCpuTimeMs_Invalid_ReturnsMinusOne(string value)
    {
        Assert.Equal(-1, ProcessTreeCpuSampler.ParseCpuTimeMs(value));
    }

    [Fact]
    public void CollectDescendants_WalksTree_AndSurvivesCycles()
    {
        var children = new Dictionary<int, List<int>>
        {
            [1] = [2, 3],
            [2] = [4],
            [4] = [1] // pathological cycle back to the root
        };

        var result = ProcessTreeCpuSampler.CollectDescendants(1, children);

        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Order());
    }

    [Fact]
    public void CollectDescendants_RootOnly_WhenNoChildren()
    {
        var result = ProcessTreeCpuSampler.CollectDescendants(
            42, new Dictionary<int, List<int>>());

        Assert.Equal(new[] { 42 }, result);
    }

    [Fact]
    public void TrySampleTreeCpuMs_SelfProcess_ReturnsNonNegative()
    {
        var sampled = ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId);

        // ps(1) may be unavailable in sandboxed environments (macOS sandbox,
        // container without procfs).  null is a valid "no signal" return;
        // only assert non-negative when a value is produced.
        if (sampled is not null)
            Assert.True(sampled >= 0, $"expected non-negative cpu ms, got {sampled}");
    }
}
