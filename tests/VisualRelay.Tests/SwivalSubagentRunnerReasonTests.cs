using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Always-on, process-free siblings for the gated stall-kill facts: the exact
/// persistent-stall reason string (the phase/threshold/socket-wedge wording the GUI
/// and autopsy key on) and the killed-output header's trace-presence count. The gated
/// real-process facts in <c>SwivalSubagentRunnerWatchdogTests</c> /
/// <c>ProcessCaptureGracefulStopTests</c> exercise these against a live kill; here the
/// same decision logic is asserted directly, with no child and no clock.
/// </summary>
public sealed class SwivalSubagentRunnerReasonTests
{
    private static ActivityWatchdog.Result Result(
        ActivityWatchdog.Outcome outcome, string lastSource, long silenceMs, long subtreeIdleForMs = 0) =>
        new(outcome, lastSource, silenceMs, subtreeIdleForMs);

    [Fact]
    public void BuildPersistentStallReason_FirstOutputPhase_NamesFirstOutputThreshold()
    {
        // No pulse ever arrived (LastPulseSource "none") → first-output phase.
        var reason = SwivalSubagentRunner.BuildPersistentStallReason(
            Result(ActivityWatchdog.Outcome.FiredStall, "none", silenceMs: 2_000),
            firstOutputMs: 2_000, inactivityMs: 600_000, maxStallAttempts: 1);

        Assert.Contains("persistent model-backend stall", reason, StringComparison.Ordinal);
        Assert.Contains("first-output", reason, StringComparison.Ordinal);
        Assert.Contains("2000ms", reason, StringComparison.Ordinal);
        Assert.Contains("1 attempts", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPersistentStallReason_InactivityPhase_NamesInactivityThreshold()
    {
        // A real pulse arrived, then silence → inactivity phase, inactivity threshold.
        var reason = SwivalSubagentRunner.BuildPersistentStallReason(
            Result(ActivityWatchdog.Outcome.FiredStall, "stdout", silenceMs: 2_000),
            firstOutputMs: 90_000, inactivityMs: 2_000, maxStallAttempts: 2);

        Assert.Contains("inactivity", reason, StringComparison.Ordinal);
        Assert.Contains("2000ms", reason, StringComparison.Ordinal);
        Assert.Contains("2 attempts", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPersistentStallReason_SocketWedge_NamesTheAdditiveDetector()
    {
        var reason = SwivalSubagentRunner.BuildPersistentStallReason(
            Result(ActivityWatchdog.Outcome.FiredSocketWedge, "cpu", silenceMs: 6_000, subtreeIdleForMs: 6_000),
            firstOutputMs: 90_000, inactivityMs: 6_000, maxStallAttempts: 3);

        Assert.Contains("socket-wedged", reason, StringComparison.Ordinal);
        Assert.Contains("ESTABLISHED", reason, StringComparison.Ordinal);
        Assert.Contains("sustained-idle", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CountTraceFiles_CountsFilesAndBytes()
    {
        using var dir = new TempDirectory();
        var traceDir = Path.Combine(dir.Path, "stage1-attempt1");
        Directory.CreateDirectory(traceDir);
        File.WriteAllText(Path.Combine(traceDir, "trace.jsonl"), "12345"); // 5 bytes
        File.WriteAllText(Path.Combine(traceDir, "other.jsonl"), "123");   // 3 bytes

        var (files, bytes) = SwivalSubagentRunner.CountTraceFiles(traceDir);

        Assert.Equal(2, files);
        Assert.Equal(8, bytes);
    }

    [Fact]
    public void CountTraceFiles_EmptyOrMissingDir_ReturnsZero()
    {
        using var dir = new TempDirectory();
        var empty = Path.Combine(dir.Path, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal((0, 0L), SwivalSubagentRunner.CountTraceFiles(empty));
        Assert.Equal((0, 0L), SwivalSubagentRunner.CountTraceFiles(Path.Combine(dir.Path, "does-not-exist")));
    }
}
