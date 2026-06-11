namespace VisualRelay.Tests;

public sealed class WaitHelpersTests
{
    [Fact]
    public async Task WaitUntilAsync_ConditionAlreadyTrue_ReturnsImmediately()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitHelpers.WaitUntilAsync(() => true);
        sw.Stop();

        // Must not have waited a full poll cycle — the very first check
        // succeeds and returns before any Task.Delay.
        Assert.True(sw.ElapsedMilliseconds < 10,
            $"expected immediate return, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WaitUntilAsync_ConditionBecomesTrue_ReturnsBeforeTimeout()
    {
        var callCount = 0;
        // Become true on the 5th poll (~100 ms).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitHelpers.WaitUntilAsync(() =>
        {
            callCount++;
            return callCount >= 5;
        });
        sw.Stop();

        Assert.Equal(5, callCount);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"expected return within ~100 ms, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WaitUntilAsync_ConditionNeverTrue_FailsAfterAllPolls()
    {
        var callCount = 0;
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            WaitHelpers.WaitUntilAsync(() =>
            {
                callCount++;
                return false;
            }));

        Assert.Equal(51, callCount); // 50 loops + 1 final assert
        Assert.Contains("Assert.True", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntilWithDispatcherAsync_ConditionBecomesTrue_FlushesDispatcher()
    {
        var callCount = 0;
        // The dispatcher-based version must not throw when called without
        // a real Avalonia dispatcher — it still polls correctly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await WaitHelpers.WaitUntilWithDispatcherAsync(() =>
        {
            callCount++;
            return callCount >= 3;
        });
        sw.Stop();

        Assert.Equal(3, callCount);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"expected return within ~60 ms, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WaitUntilWithDispatcherAsync_ConditionNeverTrue_FailsAfterAllPolls()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            WaitHelpers.WaitUntilWithDispatcherAsync(() => false));

        Assert.Contains("Assert.True", ex.ToString(), StringComparison.Ordinal);
    }
}
