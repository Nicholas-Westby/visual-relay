using System.Diagnostics;
using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the C# timeout watchdog that replaced the launcher's bash
/// <c>_timeout_watchdog</c>. It runs a child to completion, but if the child
/// outlives the deadline it kills the whole tree and returns 124 (the GNU
/// <c>timeout</c> convention the bash version used). Used by <c>test</c> and
/// <c>check</c>.
/// </summary>
public sealed class CliWatchdogTests
{
    [Fact]
    public async Task ReturnsChildExitCode_WhenChildFinishesInTime()
    {
        var sw = Stopwatch.StartNew();
        var rc = await TimeoutWatchdog.RunAsync(
            "/bin/sh", ["-c", "exit 7"], Directory.GetCurrentDirectory(),
            TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.Equal(7, rc);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9), "should return promptly, not wait the full timeout");
    }

    [Fact]
    public async Task Returns124_AndKillsTree_OnTimeout()
    {
        var sw = Stopwatch.StartNew();
        // 0-CPU, no-timer child that never exits on its own — the 500ms watchdog
        // deadline is the only thing that ends it. WaitAsync caps a REGRESSED
        // watchdog at ~8s instead of hanging forever against `tail -f` (which,
        // unlike `sleep 30`, has no self-exit ceiling of its own).
        var rc = await TimeoutWatchdog.RunAsync(
            "/bin/sh", ["-c", "exec tail -f /dev/null"], Directory.GetCurrentDirectory(),
            TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(8));
        sw.Stop();

        Assert.Equal(124, rc);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"watchdog should fire near the 0.5s deadline, took {sw.Elapsed.TotalSeconds:F1}s");
    }

    // The env-var timeout seams (VISUAL_RELAY_TEST_TIMEOUT default 60s for `test`,
    // VISUAL_RELAY_CHECK_TEST_TIMEOUT default 300s for the `check` gate) are
    // resolved by the pure WatchdogTimeouts.Resolve, tested here directly so no
    // process-global env mutation is needed (a banned pattern in tests).
    [Theory]
    [InlineData(null, 60, 60)]
    [InlineData("", 60, 60)]
    [InlineData("7", 60, 7)]
    [InlineData("nonsense", 60, 60)]
    [InlineData("0", 60, 60)]
    [InlineData("-5", 60, 60)]
    [InlineData("111", 300, 111)]
    public void Resolve_HonorsPositiveIntegerSeconds_ElseDefault(string? raw, int defaultSecs, int expectedSecs)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSecs),
            WatchdogTimeouts.Resolve(raw, defaultSecs));
    }

    [Fact]
    public async Task Returns124_EvenWhenChildIgnoresSigterm()
    {
        // A child that traps SIGTERM and keeps running must still be force-killed
        // and reported as 124 — the bash watchdog escalated TERM→KILL for this.
        var sw = Stopwatch.StartNew();
        // trap '' TERM keeps the SIG_IGN(TERM) disposition (preserved across exec),
        // so the block-forever `tail` still ignores SIGTERM and must be SIGKILL'd —
        // the same force-kill the original `sleep 30` exercised. WaitAsync caps a
        // regressed watchdog at ~8s rather than hanging forever.
        var rc = await TimeoutWatchdog.RunAsync(
            "/bin/sh", ["-c", "trap '' TERM; exec tail -f /dev/null"], Directory.GetCurrentDirectory(),
            TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(8));
        sw.Stop();

        Assert.Equal(124, rc);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"watchdog should force-kill a SIGTERM-ignoring child, took {sw.Elapsed.TotalSeconds:F1}s");
    }
}
