using System.Diagnostics;
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
    public void BuildBackendStartInfo_Unix_RunsLitellmViaDetachedShellRedirect()
    {
        var env = new Dictionary<string, string> { ["DEEPSEEK_API_KEY"] = "sk-x" };
        var psi = BackendLifecycle.BuildBackendStartInfo(
            "/venv/bin/litellm", "/venv/bin/uvicorn", "/cfg.yaml", "127.0.0.1", "4000",
            "/log/litellm.log", env, isWindows: false);

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal("-c", psi.ArgumentList[0]);
        var cmd = psi.ArgumentList[1];
        // exec replaces the shell (pid IS the proxy's), config + host/port, and a
        // truncating file redirect so the proxy detaches with no pipe back to us.
        Assert.StartsWith("exec '/venv/bin/litellm'", cmd);
        Assert.Contains("--config '/cfg.yaml'", cmd);
        Assert.Contains("--host '127.0.0.1' --port 4000", cmd);
        Assert.Contains(">'/log/litellm.log' 2>&1", cmd);
        Assert.Equal("1", psi.Environment["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("sk-x", psi.Environment["DEEPSEEK_API_KEY"]);
        Assert.False(psi.Environment.ContainsKey("CONFIG_FILE_PATH"));
    }

    [Fact]
    public void BuildBackendStartInfo_Windows_RunsUvicornViaCmdRedirect_ConfigViaEnv()
    {
        // litellm's CLI worker model crashes on Windows; run the proxy app via
        // uvicorn directly through cmd.exe, redirecting to the log file, with the
        // config passed through CONFIG_FILE_PATH.
        var psi = BackendLifecycle.BuildBackendStartInfo(
            @"C:\venv\Scripts\litellm.exe", @"C:\venv\Scripts\uvicorn.exe",
            @"C:\cfg.yaml", "127.0.0.1", "4000", @"C:\log\litellm.log",
            new Dictionary<string, string>(), isWindows: true);

        Assert.Equal("cmd.exe", psi.FileName);
        // The whole command is wrapped in one outer quote pair so cmd /c strips just
        // that pair and keeps the inner path quotes.
        Assert.StartsWith("/c \"", psi.Arguments);
        Assert.EndsWith("2>&1\"", psi.Arguments);
        Assert.Contains("\"C:\\venv\\Scripts\\uvicorn.exe\" litellm.proxy.proxy_server:app", psi.Arguments);
        Assert.Contains("--host 127.0.0.1 --port 4000", psi.Arguments);
        Assert.Contains("> \"C:\\log\\litellm.log\" 2>&1", psi.Arguments);
        Assert.Equal(@"C:\cfg.yaml", psi.Environment["CONFIG_FILE_PATH"]);
    }

    [Fact]
    public void BuildBackendStartInfo_OmitsConfig_WhenEmpty()
    {
        var unix = BackendLifecycle.BuildBackendStartInfo(
            "litellm", "uvicorn", "", "127.0.0.1", "4000", "/log", new Dictionary<string, string>(), isWindows: false);
        Assert.DoesNotContain("--config", unix.ArgumentList[1]);

        var win = BackendLifecycle.BuildBackendStartInfo(
            "litellm.exe", "uvicorn.exe", "", "127.0.0.1", "4000", @"C:\log", new Dictionary<string, string>(), isWindows: true);
        Assert.False(win.Environment.ContainsKey("CONFIG_FILE_PATH"));
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
            : new ProcessStartInfo("tail", "-f /dev/null");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }
}
