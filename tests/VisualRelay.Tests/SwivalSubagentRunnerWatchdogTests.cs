using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("Watchdog")]
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    [Fact]
    public async Task RunAsync_StallThenRecover_RetriesAndReturnsSuccess()
    {
        SlowIntegration.SkipIfNotOptedIn();

        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-stall-then-recover",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            if [[ "$trace_dir" == *attempt2* ]]; then
              mkdir -p "$trace_dir"
              printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"recovered on retry"}]}}' > "$trace_dir/trace.jsonl"
              printf '```json\n{"summary":"recovered","options":["small"]}\n```\n'
              exit 0
            else
              exec tail -f /dev/null
            fi
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 3_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 8_000,  // backstop (first-output window 3s + ~5s)
            MaxStallRetries = 1
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "balanced" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("recovered", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PersistentStall_FlagsAfterMaxRetries()
    {
        SlowIntegration.SkipIfNotOptedIn();

        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-persistent-stall",
            """
            #!/usr/bin/env bash
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
            SubagentTimeoutMilliseconds = 7_000,  // backstop (first-output window 2s + ~5s)
            MaxStallRetries = 1
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "balanced" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("2 attempts", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Expected persistent-stall flag in < 15 s, took {sw.ElapsedMilliseconds} ms");
    }

    private static RelayConfig TestConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true,
            1,
            1,
            false,
            true,
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
