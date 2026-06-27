using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Activity-watchdog regression tests (1–3), split out of
// SwivalSubagentRunnerWatchdogTests.cs to keep each file under the 300-line guard.
public sealed partial class SwivalSubagentRunnerWatchdogTests
{
    /// <summary>
    /// Regression (1): stdout bytes are liveness pulses, so a process that writes
    /// stdout but NO trace file must NOT be killed by the first-output watchdog (the
    /// original false-kill scenario). A stdout pulse sets firstPulseReceived,
    /// flipping the watchdog into the inactivity phase — so even 2.5 s elapsed (past
    /// the 2 s first-output window) with only 0.5 s of inter-pulse silence (≪ the
    /// inactivity window) stays Disarmed, exactly as a trace pulse would.
    /// </summary>
    [Fact]
    public void DecideOutcome_StdoutPulseDisarmsFirstOutput_SurvivesPastFirstOutputWindow()
    {
        Assert.Equal(
            ActivityWatchdog.Outcome.Disarmed,
            Decide(elapsedMs: 2_500, silenceMs: 500, firstPulseReceived: true,
                firstOutputTimeoutMs: 2_000, inactivityTimeoutMs: 600_000));
    }

    /// <summary>
    /// Regression (2): A totally silent process (no stdout, no stderr,
    /// no trace files) IS killed at the first-output deadline and retried.
    /// </summary>
    [Fact]
    public async Task RunAsync_TotallySilentProcess_KilledAtFirstOutputDeadline()
    {
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
            SubagentTimeoutMilliseconds = 7_000,  // backstop (first-output window 2s + ~5s)
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
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

    /// <summary>
    /// Regression (3): A process that pulses once, then goes silent
    /// past the inactivity window, and then tries to produce output —
    /// must already be killed (no resurrection).
    /// </summary>
    [Fact]
    public async Task RunAsync_SilentThenActive_KilledNoResurrection()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
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
            # Now go silent past the inactivity window (3 s for cheap) — block
            # forever so the inactivity watchdog's kill is the only thing that ends
            # this child. Any late output after exec is unreachable, exactly as the
            # kill intends (no resurrection).
            exec tail -f /dev/null
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
            SubagentTimeoutMilliseconds = 8_000,  // backstop (inactivity window 3s + ~5s)
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
        // Must have been killed by the inactivity deadline, not first-output.
        Assert.Contains("inactivity", result.Error, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Expected kill at ~3 s inactivity window, took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Unit test: drive <see cref="ActivityWatchdog"/> with a simulated
    /// pulse history matching the 2026-06-12 socket-wedge incident —
    /// pulses for the first ~2 min (simulated), then total silence for
    /// the full inactivity window.  The watchdog must fire at the
    /// inactivity deadline ± one polling interval (~200 ms).
    ///
    /// This test isolates the watchdog's deadline logic from all upstream
    /// pulse sources.  If it passes while the integration tests fail, the
    /// bug is upstream of the watchdog (in a <c>Pulse(...)</c> call site).
    /// </summary>
    [Fact]
    public async Task WaitAsync_BurstThenTotalSilence_FiresAtInactivityDeadline()
    {
        const int inactivityTimeoutMs = 2_000;
        using var kill = new CancellationTokenSource();
        var watchdog = new ActivityWatchdog(
            firstOutputTimeoutMs: 1_000,
            inactivityTimeoutMs: inactivityTimeoutMs,
            absoluteCeilingMs: 0,
            kill);

        // Simulate early output burst (representing the first ~2 min of real
        // activity: stdout lines, trace-file creation).
        watchdog.Pulse("stdout");
        // ReSharper disable once MethodSupportsCancellation — fixed-timing simulation:
        // this models a real ~50ms gap between pulses and must not be cancellable.
        await Task.Delay(50);
        watchdog.Pulse("trace");

        // Now total silence — no more pulses of any kind.
        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await watchdog.WaitAsync(cts.Token);
        sw.Stop();

        Assert.Equal(ActivityWatchdog.Outcome.FiredStall, result.Outcome);
        // Last real pulse before silence was "trace".
        Assert.Equal("trace", result.LastPulseSource);
        Assert.True(result.SilenceMs >= inactivityTimeoutMs,
            $"Expected silence >= {inactivityTimeoutMs}ms, got {result.SilenceMs}ms");
        // Must fire within inactivityTimeoutMs + generous margin for the
        // 200 ms polling loop and OS scheduling jitter.
        Assert.True(sw.ElapsedMilliseconds < inactivityTimeoutMs + 2_000,
            $"Fired too late: {sw.ElapsedMilliseconds}ms (expected ~{inactivityTimeoutMs}ms)");
        Assert.True(kill.IsCancellationRequested);
    }
}
