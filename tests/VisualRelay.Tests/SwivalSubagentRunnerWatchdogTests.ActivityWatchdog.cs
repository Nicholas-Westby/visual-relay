using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Activity-watchdog smoke tests (real process + real kill), split out of
// SwivalSubagentRunnerWatchdogTests.cs to keep each file under the 300-line guard.
// Decision-seam and virtualized WaitAsync tests moved to ActivityWatchdogDecisionTests.cs.
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    /// <summary>
    /// SMOKE: real process spawn + real kill path. Spawns a totally silent
    /// process that blocks forever; the watchdog must kill it at the first-output
    /// deadline. This is one of the 2–3 end-to-end smoke tests that remain in the
    /// Watchdog collection.
    /// </summary>
    [Fact]
    public async Task RunAsync_TotallySilentProcess_KilledAtFirstOutputDeadline()
    {
        SlowIntegration.SkipIfNotOptedIn();

        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-totally-silent",
            """
            #!/usr/bin/env bash
            # Completely silent — no output, no trace dir.
            exec tail -f /dev/null
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 2_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 7_000  // backstop (first-output window 2s + ~5s)
        };
        var runner = new SwivalSubagentRunner(config, new NullGitInvoker(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "balanced" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("first-output", result.Error, StringComparison.Ordinal);
        Assert.Contains("no activity", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s, took {sw.ElapsedMilliseconds} ms");
    }
}
