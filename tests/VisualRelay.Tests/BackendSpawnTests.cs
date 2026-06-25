using System.Diagnostics;
using System.Text;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the shell-free litellm spawn (Phase 2): the proxy is launched
/// directly (no <c>/bin/sh</c> on Unix, no <c>cmd.exe</c> on Windows) with its
/// output pumped to the log file, and the stop path terminates a real process and
/// clears the pidfile through the Windows-aware <see cref="BackendProcess"/>.
/// </summary>
public sealed class BackendSpawnTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "vr-spawn", Guid.NewGuid().ToString("N"));

    public BackendSpawnTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private BackendPaths Paths()
    {
        var paths = BackendPaths.Resolve(new DictionaryEnvironmentAccessor { ["HOME"] = _home });
        Directory.CreateDirectory(paths.Scratch);
        return paths;
    }

    // ── Shell-free start info ────────────────────────────────────────────

    [Fact]
    public void BuildLitellmStartInfo_LaunchesProxyDirectly_NoShell()
    {
        var env = new Dictionary<string, string> { ["DEEPSEEK_API_KEY"] = "sk-x" };
        var psi = BackendLifecycle.BuildLitellmStartInfo(
            @"C:\venv\Scripts\litellm.exe", @"C:\cfg.yaml", "127.0.0.1", "4000", env);

        // The litellm binary itself is the launch target — not /bin/sh or cmd.exe.
        Assert.Equal(@"C:\venv\Scripts\litellm.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal(
            new[] { "--config", @"C:\cfg.yaml", "--host", "127.0.0.1", "--port", "4000" },
            psi.ArgumentList);
        Assert.Equal("1", psi.Environment["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("sk-x", psi.Environment["DEEPSEEK_API_KEY"]);
    }

    [Fact]
    public void BuildLitellmStartInfo_OmitsConfig_WhenEmpty()
    {
        var psi = BackendLifecycle.BuildLitellmStartInfo(
            "litellm", "", "127.0.0.1", "4000", new Dictionary<string, string>());

        Assert.DoesNotContain("--config", psi.ArgumentList);
        Assert.Equal(new[] { "--host", "127.0.0.1", "--port", "4000" }, psi.ArgumentList);
    }

    // ── Log pump ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyStreamsToLogAsync_CapturesBothStdoutAndStderr()
    {
        using var stdout = new MemoryStream(Encoding.UTF8.GetBytes("proxy booting on 4000\n"));
        using var stderr = new MemoryStream(Encoding.UTF8.GetBytes("WARN config fallback\n"));
        using var log = new MemoryStream();

        await BackendLifecycle.CopyStreamsToLogAsync(stdout, stderr, log);

        var text = Encoding.UTF8.GetString(log.ToArray());
        Assert.Contains("proxy booting on 4000", text);
        Assert.Contains("WARN config fallback", text);
    }

    // ── Stop terminates a real process (Windows BackendProcess teardown) ──

    [Fact]
    public async Task StopAsync_TerminatesLiveProcess_AndRemovesPidfile()
    {
        var paths = Paths();
        using var child = StartSleeper();
        await File.WriteAllTextAsync(paths.PidFile, child.Id.ToString());

        var lifecycle = new BackendLifecycle(
            paths,
            healthCheck: _ => Task.FromResult(false),
            log: _ => { });

        var result = await lifecycle.StopAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(paths.PidFile), "stop must remove the pidfile");
        Assert.True(child.WaitForExit(5_000), "stop must terminate the process");
    }

    // A real, killable, long-lived child process on any OS.
    private static Process StartSleeper()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }
}
