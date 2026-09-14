using System.Diagnostics;
using System.Runtime.Versioning;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A cancelled command is stopped and reported as cancelled. Found tracing the crawl stage
/// on the Windows arm: on a cancel, the wait for exit and the timeout delay complete
/// together, and whichever won decided the outcome. When the delay won, the cancel came back
/// as "timed out"; when the wait won, the cancellation surfaced with the command still
/// running, because the agent's command tool passes no separate kill token.
/// </summary>
[UnsupportedOSPlatform("windows")]
[Collection("Watchdog")]
public sealed class ProcessCaptureCancelTests
{
    [Fact]
    public async Task ACancelledCommand_IsStoppedAndSurfacesAsACancellation()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "spawns a POSIX shell");
        var root = Directory.CreateTempSubdirectory("vr-cancel-").FullName;
        var pidFile = Path.Combine(root, "pid");
        try
        {
            using var cts = new CancellationTokenSource();
            var run = ProcessCapture.RunAsync(
                "/bin/sh", ["-c", $"echo $$ > '{pidFile}'; exec tail -f /dev/null"], root,
                TimeSpan.FromMinutes(5), cts.Token);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, TimeSpan.FromSeconds(30)));
            var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());

            await cts.CancelAsync();
            var thrown = await Record.ExceptionAsync(() => run);

            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            Assert.False(IsRunning(pid), $"the command (pid {pid}) is still running after its cancel");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
