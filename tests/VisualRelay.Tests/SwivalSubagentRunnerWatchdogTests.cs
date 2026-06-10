using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerWatchdogTests
{
    private static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    [Fact]
    public async Task RunAsync_StallThenRecover_RetriesAndReturnsSuccess()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "balanced" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("recovered", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PerTierThreshold_FrontierNotKilledAtCheapThreshold()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "frontier" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("frontier review passed", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CheapStallKilledAtCheapThreshold()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "cheap" });
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
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "balanced" });

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("long but alive", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PersistentStall_FlagsAfterMaxRetries()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "balanced" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("2 attempts", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Expected persistent-stall flag in < 15 s, took {sw.ElapsedMilliseconds} ms");
    }

    private static StageInvocation Invocation(string rootPath) =>
        new(
            RelayStages.All[0],
            "cheap",
            "run-1",
            rootPath,
            "task",
            "# Task",
            string.Empty,
            [],
            [],
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1"),
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1.report.json"),
            1);

    // ── New regression tests (ActivityWatchdog) ────────────────────────

    /// <summary>
    /// Regression (1): A process that writes stdout but NO trace file
    /// must NOT be killed by the first-output watchdog.  This is the
    /// original false-kill scenario: stdout bytes are visible as
    /// liveness pulses.
    /// </summary>
    [Fact]
    public async Task RunAsync_StdoutNoTraceFile_NotKilled()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-stdout-no-trace",
            """
            #!/usr/bin/env bash
            # Simulate a heavy first turn: write stdout (proxy calls
            # producing output) but do NOT create the trace directory
            # until the very end.
            for i in $(seq 1 5); do
              echo "proxy call $i done" >&1
              sleep 0.5
            done
            # Only now create the trace dir and write the final JSON.
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"done"}]}}' > "$trace_dir/trace.jsonl"
            printf '```json\n{"summary":"stdout kept me alive","options":["small"]}\n```\n'
            exit 0
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 2_000,  // would kill if stdout invisible
                ["frontier"] = 660_000
            },
            // 0 = no absolute ceiling; inactivity deadline is what matters.
            SubagentTimeoutMilliseconds = 0,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "balanced" });
        sw.Stop();

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("stdout kept me alive", result.Json, StringComparison.Ordinal);
        // Must have finished well before the 2 s first-output threshold
        // (stdout pulses disarm the watchdog).
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected completion in < 10 s, took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Regression (2): A totally silent process (no stdout, no stderr,
    /// no trace files) IS killed at the first-output deadline and retried.
    /// </summary>
    [Fact]
    public async Task RunAsync_TotallySilentProcess_KilledAtFirstOutputDeadline()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-totally-silent",
            """
            #!/usr/bin/env bash
            # Completely silent — no output, no trace dir.
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
            SubagentTimeoutMilliseconds = 15_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "balanced" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.Contains("first-output", result.Error, StringComparison.Ordinal);
        Assert.Contains("no activity", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s, took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Regression (3): A process that pulses once, then goes silent
    /// past the inactivity window, and then tries to produce output —
    /// must already be killed (no resurrection).
    /// </summary>
    [Fact]
    public async Task RunAsync_SilentThenActive_KilledNoResurrection()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
            repo.Root,
            "fake-swival-silent-then-active",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            # Pulse once via trace dir creation.
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"first token"}]}}' > "$trace_dir/trace.jsonl"
            # Now go silent past the inactivity window (3 s for cheap).
            sleep 10
            # Late output — should never be seen because the stage is already killed.
            echo "too late" >&1
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
            InactivityTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 3_000,    // 3 s window
                ["balanced"] = 600_000,
                ["frontier"] = 1_200_000
            },
            SubagentTimeoutMilliseconds = 30_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "cheap" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        // Must have been killed by the inactivity deadline, not first-output.
        Assert.Contains("inactivity", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Expected kill at ~3 s inactivity window, took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Regression (4): Periodic stdout + trace pulses every 2 s must
    /// keep a stage alive past what would have been the old flat cap
    /// (10 s).  The stage runs 16 s without a kill.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActivityPulsesExtendPastFlatCap()
    {
        using var repo = TestRepository.Create();
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "cheap" });
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
        var script = await WriteExecutableAsync(
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
        var runner = new SwivalSubagentRunner(config, script, backendProbe: AlwaysReady);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Invocation(repo.Root) with { Tier = "balanced" });
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
        var cheapScript = await WriteExecutableAsync(
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
        var cheapRunner = new SwivalSubagentRunner(cheapConfig, cheapScript, backendProbe: AlwaysReady);

        var swCheap = System.Diagnostics.Stopwatch.StartNew();
        var cheapResult = await cheapRunner.RunAsync(
            Invocation(repo.Root) with { Tier = "cheap" });
        swCheap.Stop();

        Assert.False(cheapResult.IsValid);
        Assert.Contains("persistent model-backend stall", cheapResult.Error, StringComparison.Ordinal);
        Assert.True(swCheap.ElapsedMilliseconds < 12_000,
            $"Cheap should be killed at ~3 s, took {swCheap.ElapsedMilliseconds} ms");

        // ── Frontier tier: long inactivity window → survives ──
        var frontScript = await WriteExecutableAsync(
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
        var frontRunner = new SwivalSubagentRunner(frontConfig, frontScript, backendProbe: AlwaysReady);

        var frontResult = await frontRunner.RunAsync(
            Invocation(repo.Root) with { Tier = "frontier" });

        Assert.True(frontResult.IsValid);
        Assert.Null(frontResult.Error);
        Assert.Contains("frontier survived silence", frontResult.Json, StringComparison.Ordinal);
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

    private static async Task<string> WriteExecutableAsync(string rootPath, string name, string text)
    {
        var path = Path.Combine(rootPath, name);
        await File.WriteAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
