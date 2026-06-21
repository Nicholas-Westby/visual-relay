using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Tier-window ActivityWatchdog regression tests (4–6), split out of
// SwivalSubagentRunnerWatchdogTests.cs to keep each file under the 300-line guard.
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    /// <summary>
    /// Regression (4): Periodic stdout + trace pulses every 2 s must
    /// keep a stage alive past what would have been the old flat cap
    /// (10 s).  The stage runs 16 s without a kill.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActivityPulsesExtendPastFlatCap()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-pulse-extend",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            # Pulse every 2 s for 16 s total (8 pulses).
            for i in $(seq 1 8); do
              echo "pulse $i" >&1
              printf '%s\n' "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"pulse $i\"}]}}" >> "$trace_dir/trace.jsonl"
              sleep 2
            done
            printf '```json\n{"summary":"extended past old cap","options":["small"]}\n```\n'
            exit 0
            """);
        // Old flat cap was 10 s; we set inactivity to 5 s so 2 s spacing
        // keeps us alive.  No absolute ceiling.
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 5_000,
                ["balanced"] = 600_000,
                ["frontier"] = 1_200_000
            },
            SubagentTimeoutMilliseconds = 0,  // no absolute ceiling
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "cheap" });
        sw.Stop();

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("extended past old cap", result.Json, StringComparison.Ordinal);
        // Must have survived past the old 10 s flat cap.
        Assert.True(sw.ElapsedMilliseconds >= 14_000,
            $"Expected >= 14 s runtime (past old 10 s cap), took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Regression (5): When SubagentTimeoutMilliseconds > 0 the absolute
    /// ceiling kills the stage despite continuous activity pulses.
    /// </summary>
    [Fact]
    public async Task RunAsync_AbsoluteCeilingKillsDespiteActivity()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-ceiling-kill",
            """
            #!/usr/bin/env bash
            # Pulse stdout every 1 s — continuous activity.
            for i in $(seq 1 30); do
              echo "alive $i" >&1
              sleep 1
            done
            exit 0
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 10_000,  // 10 s absolute ceiling
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "balanced" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("absolute ceiling", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Expected ceiling kill at ~10 s, took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Regression (6): Per-tier inactivity windows are honored.
    /// Cheap tier with a 3 s window is killed during 8 s silence;
    /// frontier tier with a 30 s window survives the same 8 s silence.
    /// </summary>
    [Fact]
    public async Task RunAsync_PerTierWindowsHonored()
    {
        using var repo = TestRepository.Create();

        // ── Cheap tier: short inactivity window → killed ──
        var cheapScript = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-cheap-inactivity",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"first pulse"}]}}' > "$trace_dir/trace.jsonl"
            # 8 s silence exceeds the 3 s cheap inactivity window.
            sleep 8
            printf '```json\n{"summary":"should be dead already","options":["small"]}\n```\n'
            exit 0
            """);
        var cheapConfig = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 3_000,
                ["balanced"] = 600_000,
                ["frontier"] = 600_000
            },
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 0
        };
        var cheapRunner = new SwivalSubagentRunner(cheapConfig, cheapScript, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var swCheap = System.Diagnostics.Stopwatch.StartNew();
        var cheapResult = await cheapRunner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "cheap" });
        swCheap.Stop();

        Assert.False(cheapResult.IsValid);
        Assert.Contains("persistent model-backend stall", cheapResult.Error, StringComparison.Ordinal);
        Assert.True(swCheap.ElapsedMilliseconds < 12_000,
            $"Cheap should be killed at ~3 s, took {swCheap.ElapsedMilliseconds} ms");

        // ── Frontier tier: long inactivity window → survives ──
        var frontScript = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-frontier-inactivity",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"first pulse"}]}}' > "$trace_dir/trace.jsonl"
            # 8 s silence fits within the 30 s frontier inactivity window.
            sleep 8
            printf '```json\n{"summary":"frontier survived silence","options":["small"]}\n```\n'
            exit 0
            """);
        var frontConfig = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 30_000   // generous first-output window
            },
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 600_000,
                ["balanced"] = 600_000,
                ["frontier"] = 30_000   // 30 s > 8 s silence
            },
            SubagentTimeoutMilliseconds = 60_000,
            MaxStallRetries = 0
        };
        var frontRunner = new SwivalSubagentRunner(frontConfig, frontScript, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var frontResult = await frontRunner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "frontier" });

        Assert.True(frontResult.IsValid);
        Assert.Null(frontResult.Error);
        Assert.Contains("frontier survived silence", frontResult.Json, StringComparison.Ordinal);
    }
}
