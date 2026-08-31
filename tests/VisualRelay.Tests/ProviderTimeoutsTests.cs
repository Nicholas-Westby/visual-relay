using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Pins the measured default budgets. These are not arbitrary: the old single
/// stream timeout of 600 s was justified by a NON-streaming observation, while
/// streaming time-to-first-byte measured under 1.2 s on every reasoning model.
/// </summary>
public sealed class ProviderTimeoutsTests
{
    /// <summary>The four defaults, each separately meaningful.</summary>
    [Fact]
    public void Defaults_AreTheMeasuredBudgets()
    {
        var timeouts = ProviderTimeouts.Default;

        Assert.Equal(TimeSpan.FromSeconds(10), timeouts.Connect);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.TimeToFirstByte);
        Assert.Equal(TimeSpan.FromSeconds(45), timeouts.InterChunkIdle);
        Assert.Equal(TimeSpan.FromSeconds(600), timeouts.Total);
    }

    /// <summary>
    /// The idle budget keeps a wide margin over the worst inter-chunk gap ever
    /// measured (1.12 s across 1502 chunks), so it fires only on a real wedge.
    /// </summary>
    [Fact]
    public void IdleBudget_KeepsAWideMarginOverTheWorstMeasuredGap()
    {
        var worstMeasuredGap = TimeSpan.FromSeconds(1.12);

        Assert.True(ProviderTimeouts.Default.InterChunkIdle > worstMeasuredGap * 30);
    }

    /// <summary>Each budget is strictly tighter than the one it backstops.</summary>
    [Fact]
    public void Budgets_AreOrderedFromTightestToLoosest()
    {
        var t = ProviderTimeouts.Default;

        Assert.True(t.Connect < t.TimeToFirstByte);
        Assert.True(t.TimeToFirstByte < t.InterChunkIdle);
        Assert.True(t.InterChunkIdle < t.Total);
    }
}
