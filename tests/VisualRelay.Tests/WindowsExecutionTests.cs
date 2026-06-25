using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Windows task-execution tests (Phase 3): git resolves through the PATHEXT helper
/// (no xcrun, no <c>/bin/sh</c> fallback), and the shell test runner wraps the
/// user's configured command in <c>cmd.exe /c</c> instead of <c>/bin/sh -lc</c>.
/// The OS dispatch is a pure helper asserted on any OS; the real-git and real-shell
/// cases are gated to Windows.
/// </summary>
public sealed class WindowsExecutionTests
{
    // ── ShellTestRunner OS dispatch (pure) ───────────────────────────────

    [Fact]
    public void BuildShellLaunch_Windows_UsesCmdC()
    {
        var (fileName, args) = ShellTestRunner.BuildShellLaunch("dotnet test", isWindows: true);

        Assert.Equal("cmd.exe", fileName);
        Assert.Equal(new[] { "/c", "dotnet test" }, args);
    }

    [Fact]
    public void BuildShellLaunch_Unix_UsesBinShLoginC()
    {
        var (fileName, args) = ShellTestRunner.BuildShellLaunch("dotnet test", isWindows: false);

        Assert.Equal("/bin/sh", fileName);
        Assert.Equal(new[] { "-lc", "dotnet test" }, args);
    }

    // ── ShellTestRunner runs a real command through cmd.exe on Windows ────

    [Fact]
    public async Task ShellTestRunner_OnWindows_RunsCommandViaCmd()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows cmd.exe execution");
        using var repo = TestRepository.Create();
        var runner = new ShellTestRunner(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(repo.Root, "echo hello-from-cmd");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-from-cmd", result.Output, StringComparison.Ordinal);
    }

    // ── Process-tree teardown on timeout (Windows) ───────────────────────

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
            await Task.Delay(1500); // a few heartbeat intervals
            Assert.Equal(sizeAtKill, new FileInfo(hbFile).Length); // no further writes => tree killed
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
