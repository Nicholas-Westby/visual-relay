using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("Watchdog")]
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    [Fact]
    public async Task RunAsync_StallThenRecover_RetriesAndReturnsSuccess()
    {
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
              sleep 60
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
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 1
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "balanced" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("recovered", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PerTierThreshold_FrontierNotKilledAtCheapThreshold()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-frontier-slow-start",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            sleep 3
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"frontier thinking done"}]}}' > "$trace_dir/trace.jsonl"
            printf '```json\n{"summary":"frontier review passed","options":["small"]}\n```\n'
            exit 0
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 30_000
            },
            SubagentTimeoutMilliseconds = 15_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "frontier" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("frontier review passed", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CheapStallKilledAtCheapThreshold()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-cheap-stall",
            """
            #!/usr/bin/env bash
            sleep 60
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 15_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "cheap" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s threshold, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RunAsync_SlowButAlive_WatchdogDisarmsAfterFirstOutput()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-slow-but-alive",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"first token emitted"}]}}' > "$trace_dir/trace.jsonl"
            sleep 15
            printf '```json\n{"summary":"long but alive","options":["small"]}\n```\n'
            exit 0
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 10_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "balanced" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("long but alive", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PersistentStall_FlagsAfterMaxRetries()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-persistent-stall",
            """
            #!/usr/bin/env bash
            sleep 60
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 2_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 60_000,
            MaxStallRetries = 1
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

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
            1,
            1,
            1,
            false,
            true,
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            BypassSandbox: true,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
