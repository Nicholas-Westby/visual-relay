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

    /// <summary>
    /// Per-tier first-output window (frontier vs cheap), sleep-free. Before the first
    /// pulse the decision compares elapsed-from-start to the tier's first-output
    /// window: a 3 s slow start FiresStall at cheap's 2 s window but is Disarmed at
    /// frontier's 30 s window — so a frontier stage is not killed at the cheap
    /// threshold. (The runner picking frontier's window, not cheap's, is pinned by
    /// ResolveTierWindows in the TierWindows partial.)
    /// </summary>
    [Fact]
    public void DecideOutcome_BeforeFirstOutput_FrontierWindowSurvivesSlowStartThatKillsCheap()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.FiredStall,
            Decide(elapsedMs: 3_000, firstPulseReceived: false, firstOutputTimeoutMs: 2_000));

        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 3_000, firstPulseReceived: false, firstOutputTimeoutMs: 30_000));
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
            exec tail -f /dev/null
            """);
        var config = TestConfig() with
        {
            FirstOutputTimeoutMsByTier = new Dictionary<string, int>
            {
                ["cheap"] = 2_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            SubagentTimeoutMilliseconds = 7_000,  // backstop (first-output window 2s + ~5s)
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            SwivalTestHelpers.Invocation(repo.Root) with { Tier = "cheap" });
        sw.Stop();

        Assert.False(result.IsValid);
        Assert.Contains("persistent model-backend stall", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"Expected kill at ~2 s threshold, took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Slow-but-alive: once the first pulse arrives the watchdog permanently leaves
    /// the first-output phase for the inactivity phase. So a long quiet stretch
    /// (15 s) that is well under the inactivity window (600 s) stays Disarmed EVEN
    /// THOUGH it is past the 10 s first-output deadline — first output disarms that
    /// deadline for good, so a genuinely slow-but-alive stage is never killed.
    /// </summary>
    [Fact]
    public void DecideOutcome_AfterFirstOutput_LongSilenceUnderInactivityWindow_Disarmed()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 15_000, silenceMs: 15_000, firstPulseReceived: true,
                firstOutputTimeoutMs: 10_000, inactivityTimeoutMs: 600_000));
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

    // Thin wrapper over the pure production decision (ActivityWatchdog.DecideOutcome)
    // so each test sets only the fields its scenario exercises. The inert defaults
    // (huge windows, no ceiling, no wedge sample) mean the decision is Disarmed
    // unless a real threshold is crossed — every fire is therefore explicit.
    private static ActivityWatchdog.Outcome Decide(
        long elapsedMs = 0,
        long silenceMs = 0,
        long realOutputSilenceMs = 0,
        long subtreeIdleForMs = 0,
        bool firstPulseReceived = true,
        int firstOutputTimeoutMs = 3_600_000,
        int inactivityTimeoutMs = 3_600_000,
        int absoluteCeilingMs = 0,
        ActivityWatchdog.WedgeSample sample = default) =>
        ActivityWatchdog.DecideOutcome(
            elapsedMs, silenceMs, realOutputSilenceMs, subtreeIdleForMs,
            firstPulseReceived, firstOutputTimeoutMs, inactivityTimeoutMs,
            absoluteCeilingMs, sample);

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
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
