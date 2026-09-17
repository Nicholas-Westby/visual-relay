using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Windows execution tests. Every sandboxed run and every workspace git call on
/// Windows goes through wsl.exe, and with no usable distro there is no native
/// fallback at all: a command checked through cmd.exe would be checked where the
/// pipeline will never run it, and would run the operator's own test command on the
/// Windows host with no sandbox. What remains native is the host-side tree kill for
/// native children such as the backend process, and git resolving through the
/// PATHEXT helper. The launch shape is a pure helper asserted on any OS; the
/// real-git and real-shell cases are gated to Windows.
/// </summary>
public sealed class WindowsExecutionTests
{
    // ── ShellTestRunner launch shape (pure) ──────────────────────────────

    [Fact]
    public void BuildShellLaunch_UsesBinShLoginC()
    {
        var (fileName, args) = ShellTestRunner.BuildShellLaunch("dotnet test");

        Assert.Equal("/bin/sh", fileName);
        Assert.Equal(new[] { "-lc", "dotnet test" }, args);
    }

    // ── Process-tree teardown on timeout (Windows, native children) ──────

    [Fact]
    public async Task ProcessCapture_OnWindows_TimeoutKillsChildTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process-tree teardown");
        var dir = Path.Combine(Path.GetTempPath(), "vr-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A grandchild (hb.cmd, started in the background) appends to the
            // heartbeat in a loop while the parent (runner.cmd) loops forever. Only
            // an entire-tree kill stops the grandchild — a parent-only kill would
            // leave it writing. Absolute paths: this host's cmd does not search the
            // cwd for a command name (NoDefaultCurrentDirectoryInExePath).
            var hbFile = Path.Combine(dir, "hb.txt");
            var hbCmd = Path.Combine(dir, "hb.cmd");
            var runnerCmd = Path.Combine(dir, "runner.cmd");
            await File.WriteAllTextAsync(hbCmd,
                $"@echo off\r\n:l\r\n>>\"{hbFile}\" echo .\r\nping -n 2 127.0.0.1 >nul\r\ngoto l\r\n");
            await File.WriteAllTextAsync(runnerCmd,
                $"@echo off\r\nstart \"\" /b cmd /c \"{hbCmd}\"\r\n:w\r\nping -n 10 127.0.0.1 >nul\r\ngoto w\r\n");

            var (_, _, timedOut) = await ProcessCapture.RunAsync(
                "cmd.exe", new[] { "/c", runnerCmd }, dir,
                TimeSpan.FromSeconds(2), CancellationToken.None);

            Assert.True(timedOut, "the long-running tree must hit the timeout");

            Assert.True(File.Exists(hbFile), "the grandchild must have written before the kill");
            var sizeAtKill = new FileInfo(hbFile).Length;
            // A still-living grandchild would grow the heartbeat file within a couple
            // of its ~1 s ping intervals; a killed tree leaves the size frozen. Poll
            // over a bounded window with SHORT cancellable waits (no real sleep) and
            // assert the size never grows — a stronger check than a single post-wait
            // read, and sleep-free per the real-sleep guard.
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!settle.IsCancellationRequested)
            {
                try { await Task.Delay(200, settle.Token); }
                catch (OperationCanceledException) { break; }
                Assert.Equal(sizeAtKill, new FileInfo(hbFile).Length); // no further writes => tree killed
            }
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(dir);
        }
    }

    // ── GitInvoker resolves git.exe on Windows ───────────────────────────

    [Fact]
    public async Task GitInvoker_OnWindows_ResolvesGitExe_AndRunsVersion()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows git resolution");
        using var repo = TestRepository.Create();
        var invoker = new GitInvoker();

        var (exitCode, output, _) = await invoker.RunAsync(
            repo.Root, ["--version"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("git version", output, StringComparison.OrdinalIgnoreCase);
    }
}
