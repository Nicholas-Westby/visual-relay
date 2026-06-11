using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    // Regression for the 2026-06-10 false-kill class: a subagent whose stdout/
    // stderr go quiet and whose trace-dir view is frozen (virtio-fs) but whose
    // process tree is actively working must not be inactivity-killed. The fake
    // burns CPU silently in a child process longer than the inactivity window,
    // then produces a valid contract.
    [Fact]
    public async Task RunAsync_SilentCpuBurn_SurvivesInactivityWindow()
    {
        // Probe: cpu sampling requires ps(1) which may be absent in sandboxed
        // macOS environments.  When unavailable the watchdog cannot pulse on
        // cpu activity and the test is inherently unwinnable — skip cleanly.
        if (ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId) is null)
            return;

        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-silent-cpu-burn",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            echo "startup chatter" >&2
            ( end=$((SECONDS+9)); while [ $SECONDS -lt $end ]; do :; done )
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"silent work done"}]}}' > "$trace_dir/trace.jsonl"
            printf '```json\n{"summary":"survived silent burn","options":["small"]}\n```\n'
            exit 0
            """);
        var config = TestConfig() with
        {
            InactivityTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 6_000 },
            SubagentTimeoutMilliseconds = 60_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.True(result.IsValid, $"expected survival via cpu pulses, got error: {result.Error}");
        Assert.Contains("survived silent burn", result.Json, StringComparison.Ordinal);
    }

    // A truly wedged subagent (no output, no trace, ~zero CPU — blocked at byte 0)
    // must still be killed by the inactivity deadline with cpu sampling active.
    [Fact]
    public async Task RunAsync_TrueWedge_NoCpu_StillStallKilled()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-true-wedge",
            """
            #!/usr/bin/env bash
            echo "startup chatter" >&2
            sleep 14
            """);
        var config = TestConfig() with
        {
            InactivityTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 4_500 },
            SubagentTimeoutMilliseconds = 60_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
    }

    // A stall-killed attempt must leave its captured stdout/stderr on disk —
    // the 2026-06-10 triple-stall autopsy had zero evidence to read.
    [Fact]
    public async Task RunAsync_StallKill_PersistsCapturedOutput()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-wedge-with-stderr",
            """
            #!/usr/bin/env bash
            echo "WEDGE-MARKER-XYZ pre-hang diagnostics" >&2
            sleep 14
            """);
        var config = TestConfig() with
        {
            InactivityTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 3_000 },
            SubagentTimeoutMilliseconds = 60_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        var persisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt1.killed-output.txt");
        Assert.True(File.Exists(persisted), $"expected killed-attempt output at {persisted}");
        var content = await File.ReadAllTextAsync(persisted);
        Assert.Contains("WEDGE-MARKER-XYZ", content, StringComparison.Ordinal);
        Assert.Contains("stall", content, StringComparison.OrdinalIgnoreCase);
    }
}
