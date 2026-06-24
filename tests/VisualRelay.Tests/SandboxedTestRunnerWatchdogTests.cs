using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the sandboxed test-run idle-reap decision logic:
/// <see cref="SandboxedTestRunner.TryParseInnerExitCode"/> (reads nono's
/// "Command exited with code N" completion marker) and
/// <see cref="SandboxedTestRunner.InterpretWatched"/> (maps a watched wrapper run
/// to a red/green/halt <see cref="TestRunResult"/>). Pure functions — no process.
/// </summary>
public sealed class SandboxedTestRunnerWatchdogTests
{
    // ── TryParseInnerExitCode ───────────────────────────────────────────

    [Theory]
    [InlineData("Command exited with code 1", 1)]
    [InlineData("Command exited with code 0", 0)]
    [InlineData("Failed: 1, Passed: 1857\nCommand exited with code 1\n", 1)]
    [InlineData("command exited with code 137", 137)] // case-insensitive
    public void TryParseInnerExitCode_MarkerPresent_ReturnsCode(string output, int expected)
    {
        Assert.Equal(expected, SandboxedTestRunner.TryParseInnerExitCode(output));
    }

    [Fact]
    public void TryParseInnerExitCode_MarkerAbsent_ReturnsNull()
    {
        Assert.Null(SandboxedTestRunner.TryParseInnerExitCode("Passed: 5\nno completion line here"));
    }

    [Fact]
    public void TryParseInnerExitCode_MultipleMarkers_ReturnsLast()
    {
        // nono prints the marker AFTER the inner command's own output, so the
        // LAST occurrence is its final verdict.
        Assert.Equal(2, SandboxedTestRunner.TryParseInnerExitCode(
            "Command exited with code 0\n...\nCommand exited with code 2"));
    }

    // ── InterpretWatched ────────────────────────────────────────────────

    [Fact]
    public void InterpretWatched_HardCapTimedOut_ReportedAsTimeout()
    {
        // Busy-forever hang: the CPU pulse kept the watchdog from firing, so the
        // hard wall-clock cap fired. Must surface as a timeout/halt.
        var r = SandboxedTestRunner.InterpretWatched(
            wrapperExitCode: -1, output: "busy output", hardCapTimedOut: true,
            reapedOnIdle: false, hardCapMs: 600_000, elapsed: TimeSpan.FromMinutes(10));

        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("test command timed out", r.Output);
        Assert.Contains("busy output", r.Output);
    }

    [Fact]
    public void InterpretWatched_MarkerPresent_CleanExit_ReportsRealRed()
    {
        var r = SandboxedTestRunner.InterpretWatched(
            wrapperExitCode: 1, output: "Failed: 1\nCommand exited with code 1",
            hardCapTimedOut: false, reapedOnIdle: false, hardCapMs: 600_000,
            elapsed: TimeSpan.FromSeconds(3));

        Assert.False(r.TimedOut);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void InterpretWatched_MarkerPresent_ReapedOnIdle_ReportsRealResult_NotKillSignal()
    {
        // The bug fix: tests FINISHED (marker present), then the wrapper lingered
        // and was reaped on idle. The wrapper's own exit code is the kill signal
        // (137) and must be IGNORED in favour of the inner code from the marker.
        var rRed = SandboxedTestRunner.InterpretWatched(
            wrapperExitCode: 137, output: "Failed: 1\nCommand exited with code 1",
            hardCapTimedOut: false, reapedOnIdle: true, hardCapMs: 600_000,
            elapsed: TimeSpan.FromSeconds(150));
        Assert.False(rRed.TimedOut);
        Assert.Equal(1, rRed.ExitCode);

        var rGreen = SandboxedTestRunner.InterpretWatched(
            wrapperExitCode: 137, output: "Passed!\nCommand exited with code 0",
            hardCapTimedOut: false, reapedOnIdle: true, hardCapMs: 600_000,
            elapsed: TimeSpan.FromSeconds(150));
        Assert.False(rGreen.TimedOut);
        Assert.Equal(0, rGreen.ExitCode);
    }

    [Fact]
    public void InterpretWatched_ReapedOnIdle_NoMarker_ReportedAsTimeout()
    {
        // Reaped on idle but the inner command never reported completion: a stall
        // (or silent-from-start). Never report a fabricated red/green — halt.
        var r = SandboxedTestRunner.InterpretWatched(
            wrapperExitCode: 137, output: "partial output, no completion marker",
            hardCapTimedOut: false, reapedOnIdle: true, hardCapMs: 600_000,
            elapsed: TimeSpan.FromSeconds(60));

        Assert.True(r.TimedOut);
        Assert.Contains("test command timed out", r.Output);
    }

    [Fact]
    public void InterpretWatched_CleanExit_NoMarker_TrustsWrapperExitCode()
    {
        // The wrapper exited on its own without a parseable marker (a non-nono
        // direct exec, or nono that exited cleanly): trust its own exit code.
        var r = SandboxedTestRunner.InterpretWatched(
            wrapperExitCode: 3, output: "some test output",
            hardCapTimedOut: false, reapedOnIdle: false, hardCapMs: 600_000,
            elapsed: TimeSpan.FromSeconds(2));

        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
    }
}
