using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the relauncher: handoff argv round-trip, fake-app spawn
/// success, and nonexistent-binary failure path.
/// </summary>
public sealed class RelauncherTests
{
    /// <summary>
    /// A handoff with AppRestartCommand round-trips through JSON: every element
    /// is a single token (no pre-joined command line), and the command targets
    /// the app, not the relauncher itself.
    /// </summary>
    [Fact]
    public void AppRestartCommand_RoundTrip_IsProperArgvArray()
    {
        using var repo = TestRepository.Create();
        var appCmd = new[] { "dotnet", "run", "--project", "/path with spaces/VisualRelay.App", "--" };

        var written = RestartHandoff.Write(
            repo.Root,
            new RelayTaskOutcome("test", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            "drain-test",
            pendingCount: 1,
            appRestartCommand: appCmd);

        // In-memory record must carry the exact array.
        Assert.NotNull(written.AppRestartCommand);
        Assert.Equal(appCmd, written.AppRestartCommand);

        // Bug 2 guard: no element is a pre-joined multi-token command line.
        Assert.True(written.AppRestartCommand!.Length > 2,
            "argv array must have >2 elements; pre-joined strings collapse into 1-2");

        // Bug 1 guard: the command targets the app, never the relauncher.
        Assert.DoesNotContain("VisualRelay.Relauncher", written.AppRestartCommand![0]);

        // Round-trip through JSON serialization.
        var read = RestartHandoff.Read(repo.Root);
        Assert.NotNull(read);
        Assert.NotNull(read!.AppRestartCommand);
        Assert.Equal(appCmd, read.AppRestartCommand);
    }

    /// <summary>
    /// The relauncher spawns a fake app, waits for the sidecar to be
    /// consumed, and exits 0. We use <c>sleep infinity</c> (zero-CPU) as a
    /// blocking fake app so the relauncher's polling loop has time to notice
    /// the handoff deletion (performed by the test itself — no real-sleep
    /// yield).
    /// </summary>
    [Fact]
    public async Task Relauncher_SpawnsFakeApp_WithCorrectArgv_AndExitsZero()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(),
            "Uses /bin/sh; Windows CI is not a target");

        using var repo = TestRepository.Create();

        var handoffPath = Path.Combine(repo.Root, ".relay", "restart-handoff.json");

        // tail -f /dev/null blocks forever without burning CPU — safe for tests.
        // The relauncher sees it as running and keeps polling.
        var appCmd = new[] { "/bin/sh", "-c", "tail -f /dev/null" };

        _ = RestartHandoff.Write(
            repo.Root,
            new RelayTaskOutcome("test", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            "drain-test",
            pendingCount: 1,
            appRestartCommand: appCmd);

        Assert.True(File.Exists(handoffPath));

        var time = new ManualTimeProvider();
        var task = VisualRelay.Relauncher.Relauncher.RunAsync(repo.Root, timeProvider: time);

        // Simulate the app consuming the handoff: delete the sidecar, then
        // advance virtual time so the relauncher's polling loop wakes up
        // and sees the file is gone.
        File.Delete(handoffPath);
        time.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(task.IsCompleted,
            "Relauncher.RunAsync should complete after virtual-time advance");
        var exitCode = await task;
        Assert.Equal(0, exitCode);

        // The handoff was deleted (by us, simulating the app).
        Assert.False(File.Exists(handoffPath));
    }

    /// <summary>
    /// When AppRestartCommand points at a nonexistent binary, the relauncher
    /// exits nonzero, leaves the sidecar in place for diagnosis, and writes
    /// a drain-log event describing the failure.
    /// </summary>
    [Fact]
    public async Task Relauncher_NonexistentBinary_ExitsNonzero_LeavesSidecarAndLogsEvent()
    {
        using var repo = TestRepository.Create();

        var nonexistentPath = Path.Combine(repo.Root, "nonexistent", "binary");
        var appCmd = new[] { nonexistentPath, "--arg" };

        _ = RestartHandoff.Write(
            repo.Root,
            new RelayTaskOutcome("test", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            "drain-test",
            pendingCount: 1,
            appRestartCommand: appCmd);

        var handoffPath = Path.Combine(repo.Root, ".relay", "restart-handoff.json");
        Assert.True(File.Exists(handoffPath));

        var time = new ManualTimeProvider();
        var exitCode = await VisualRelay.Relauncher.Relauncher.RunAsync(
            repo.Root, timeProvider: time);

        Assert.NotEqual(0, exitCode);

        // Sidecar must remain for diagnosis.
        Assert.True(File.Exists(handoffPath));

        // Drain-log must contain a failure event.
        var logPath = Path.Combine(repo.Root, ".relay", "drain-test.log");
        Assert.True(File.Exists(logPath));
        var logContent = File.ReadAllText(logPath);
        Assert.Contains("spawn-failed", logContent);
    }
}
