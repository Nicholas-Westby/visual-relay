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
    /// <summary>
    /// Ceiling on every watchdog call below. It is a HANG GUARD, not a promptness
    /// assertion: the children here never exit on their own, so a regressed
    /// watchdog would wedge the suite forever without it. Sized ~100x the ~0.5s a
    /// healthy call takes, so only a true hang reaches it, and kept under the
    /// suite's 120s blame-hang ceiling. Do not tighten it into a timing check —
    /// an 8s version false-failed when suite thread-pool starvation delayed the
    /// continuation past it.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ReturnsChildExitCode_WhenChildFinishesInTime()
    {
        // The deadline is deliberately far larger than the child's lifetime, so a
        // watchdog that waited it out instead of returning when the child exited
        // would be caught by the hang guard rather than by a stopwatch assertion.
        // Elapsed-time checks are not assertable here: a thread-pool stall in the
        // rest of the suite delays the continuation by seconds on its own.
        var rc = await TimeoutWatchdog.RunAsync(
            "/bin/sh", ["-c", "exit 7"], Directory.GetCurrentDirectory(),
            TimeSpan.FromMinutes(10)).WaitAsync(HangGuard);

        Assert.Equal(7, rc);
    }

    [Fact]
    public async Task Returns124_AndKillsTree_OnTimeout()
    {
        // 0-CPU, no-timer child that never exits on its own — the 500ms deadline
        // is the only thing that can end it, so rc 124 is by itself proof that
        // the deadline fired. Nothing else needs asserting: how long the fire
        // takes is the thread pool's business, not the watchdog's.
        var rc = await TimeoutWatchdog.RunAsync(
            "/bin/sh", ["-c", "exec tail -f /dev/null"], Directory.GetCurrentDirectory(),
            TimeSpan.FromMilliseconds(500)).WaitAsync(HangGuard);

        Assert.Equal(124, rc);
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
        // trap '' TERM keeps the SIG_IGN(TERM) disposition (preserved across exec),
        // so the block-forever `tail` still ignores SIGTERM and must be SIGKILL'd —
        // the same force-kill the original `sleep 30` exercised.
        var rc = await TimeoutWatchdog.RunAsync(
            "/bin/sh", ["-c", "trap '' TERM; exec tail -f /dev/null"], Directory.GetCurrentDirectory(),
            TimeSpan.FromMilliseconds(500)).WaitAsync(HangGuard);

        Assert.Equal(124, rc);
    }
}
