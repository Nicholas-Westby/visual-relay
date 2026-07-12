using VisualRelay.App;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="DisplayReadyGate.WaitUntilReady"/>
/// — purely through the injected seams, no CoreVideo, no processes,
/// platform-independent.
/// </summary>
public sealed class DisplayReadyGateTests
{
    [Fact]
    public void ProbeImmediatelyZero_ReturnsTrueWithoutWake()
    {
        var wakeCalls = 0;
        var probeCalls = 0;

        var ready = DisplayReadyGate.WaitUntilReady(
            probe: () => { probeCalls++; return 0; },
            wake: () => wakeCalls++,
            delay: () => { },
            maxAttempts: 10);

        Assert.True(ready);
        Assert.Equal(0, wakeCalls);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public void ProbeFailsTwiceThenZero_ReturnsTrueAfterTwoWakes()
    {
        var callOrder = new List<string>();
        var probeResults = new Queue<int>([-6661, -6661, 0]);

        var ready = DisplayReadyGate.WaitUntilReady(
            probe: () =>
            {
                var result = probeResults.Dequeue();
                callOrder.Add($"probe→{result}");
                return result;
            },
            wake: () => callOrder.Add("wake"),
            delay: () => callOrder.Add("delay"),
            maxAttempts: 10);

        Assert.True(ready);
        Assert.Equal(
            ["probe→-6661", "wake", "delay", "probe→-6661", "wake", "delay", "probe→0"],
            callOrder);
    }

    [Fact]
    public void ProbeAlwaysFails_ReturnsFalseAfterMaxAttempts()
    {
        var probeCalls = 0;

        var ready = DisplayReadyGate.WaitUntilReady(
            probe: () => { probeCalls++; return -6661; },
            wake: () => { },
            delay: () => { },
            maxAttempts: 3);

        Assert.False(ready);
        Assert.Equal(3, probeCalls);
    }

    [Fact]
    public void WakeThrows_DoesNotAbortLoop()
    {
        var probeResults = new Queue<int>([-6661, -6661, 0]);
        var probeCalls = 0;

        var ready = DisplayReadyGate.WaitUntilReady(
            probe: () =>
            {
                probeCalls++;
                return probeResults.Dequeue();
            },
            wake: () => throw new InvalidOperationException("wake failed"),
            delay: () => { },
            maxAttempts: 10);

        Assert.True(ready);
        Assert.Equal(3, probeCalls); // -6661, -6661, 0
    }
}
