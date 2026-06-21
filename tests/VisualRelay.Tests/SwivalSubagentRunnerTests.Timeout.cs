using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Timeout tests, split out of SwivalSubagentRunnerTests.cs to keep each file
// under the 300-line guard. Uses TestConfig from the main partial class.
public sealed partial class SwivalSubagentRunnerTests
{
    [Fact]
    public async Task RunAsync_TimeoutWithNoOutput_ReportsStalledBackend()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-stalled-backend",
            """
            #!/usr/bin/env bash
            sleep 60
            """);
        // Totally silent process: no stdout, no stderr, no trace dir.
        // The first-output watchdog kills it at the per-tier window.
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 15_000,  // backstop
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("first-output", result.Error, StringComparison.Ordinal);
        Assert.Contains("2000ms", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s first-output deadline, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RunAsync_TimeoutWithPartialOutput_ReportsHungTestCommand()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-hung-test",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            echo "partial test output" >&2
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"test","message":"running suite"}' > "$trace_dir/trace.jsonl"
            sleep 60
            """);
        // Produces output (disarms first-output watchdog), then goes silent.
        // The inactivity deadline kills it after the per-tier window.
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,       // short inactivity window
                ["balanced"] = 600_000,
                ["frontier"] = 1_200_000
            },
            SubagentTimeoutMilliseconds = 30_000,  // backstop
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("inactivity", result.Error, StringComparison.Ordinal);
        Assert.Contains("2000ms", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s inactivity deadline, took {sw.ElapsedMilliseconds} ms");
    }
}
