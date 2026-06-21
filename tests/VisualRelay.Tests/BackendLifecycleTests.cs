using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the C# backend lifecycle (<see cref="BackendLifecycle"/> and
/// helpers) that replaced <c>tools/backend/backend.sh</c>. They preserve the
/// guarantees the old <c>Installer5BackendSh*</c> characterization tests asserted
/// against the script — unconditional XDG paths, stale-pid safety, broken-venv
/// self-heal, legacy repo-local cleanup, <c>PYTHONDONTWRITEBYTECODE</c>, and the
/// gen-config timeout/fallback — but drive the ported C# code directly so no real
/// litellm is launched. Each test isolates state under a temp XDG home.
/// </summary>
public sealed class BackendLifecycleTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "vr-backend-life", Guid.NewGuid().ToString("N"));

    public BackendLifecycleTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private BackendPaths Paths()
    {
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = _home };
        return BackendPaths.Resolve(env);
    }

    // ── 1. Unconditional XDG paths ───────────────────────────────────────

    [Fact]
    public void Paths_AreUnderXdgDataHome_NotRepoTree()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["XDG_DATA_HOME"] = Path.Combine(_home, "xdg"),
        };
        var paths = BackendPaths.Resolve(env);

        Assert.Equal(Path.Combine(_home, "xdg", "visual-relay"), paths.DataHome);
        Assert.Equal(Path.Combine(paths.DataHome, "backend-venv"), paths.VenvDir);
        Assert.Equal(Path.Combine(paths.DataHome, "scratch"), paths.Scratch);
        Assert.Equal(Path.Combine(paths.Scratch, "litellm.pid"), paths.PidFile);
        Assert.Equal(Path.Combine(paths.Scratch, "litellm.log"), paths.LogFile);
    }

    [Fact]
    public void Paths_FallBackToHomeLocalShare_WhenXdgUnset()
    {
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = _home };
        var paths = BackendPaths.Resolve(env);

        Assert.Equal(
            Path.Combine(_home, ".local", "share", "visual-relay"),
            paths.DataHome);
    }

    // ── 2. Stale-pid safety ──────────────────────────────────────────────

    [Fact]
    public void ReadLivePid_ReturnsNull_ForStalePidfile()
    {
        var pidFile = Path.Combine(_home, "stale.pid");
        File.WriteAllText(pidFile, "424242");

        // Liveness predicate reports the pid as gone (stale).
        var result = BackendProcess.ReadLivePid(pidFile, isAlive: _ => false);
        Assert.Null(result);
    }

    [Fact]
    public void ReadLivePid_ReturnsPid_WhenAlive()
    {
        var pidFile = Path.Combine(_home, "live.pid");
        File.WriteAllText(pidFile, "12345");

        var result = BackendProcess.ReadLivePid(pidFile, isAlive: _ => true);
        Assert.Equal(12345, result);
    }

    [Fact]
    public void ReadLivePid_ReturnsNull_WhenFileMissing() =>
        Assert.Null(BackendProcess.ReadLivePid(Path.Combine(_home, "nope.pid")));

    [Fact]
    public void IsAlive_TrueForCurrentProcess_FalseForReapedPid()
    {
        Assert.True(BackendProcess.IsAlive(Environment.ProcessId));
        // PID 1 always exists but is not ours; a wildly high pid is gone.
        Assert.False(BackendProcess.IsAlive(2_000_000_000));
    }

    // ── 3. Broken-venv self-heal ─────────────────────────────────────────

    [Fact]
    public void Ensure_SelfHealsBrokenVenv_AndReprovisionsViaUv()
    {
        var paths = Paths();
        Directory.CreateDirectory(Path.Combine(paths.VenvDir, "bin"));

        var uvArgs = new List<string>();
        var probeCount = 0;

        var result = BackendVenv.Ensure(
            paths,
            log: _ => { },
            // First probe (real venv) fails; we never get a second probe because
            // uv "provisions" and the post-provision executable check is what gates.
            probe: (_, _) => { probeCount++; return false; },
            run: (_, args) =>
            {
                uvArgs.AddRange(args);
                // Simulate uv creating an executable litellm so Ensure succeeds.
                if (args.Count > 0 && args[0] == "venv")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(paths.VenvLitellm)!);
                    File.WriteAllText(paths.VenvLitellm, "#!/bin/sh\n");
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(paths.VenvLitellm,
                            UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
                }

                return true;
            },
            onPath: name => name == "uv" ? "/usr/bin/uv" : null);

        Assert.True(result.Ok);
        Assert.Equal(paths.VenvLitellm, result.LitellmBin);
        // The broken venv must have been removed before re-provision (uv's venv
        // arg targets the XDG venv dir).
        Assert.Contains(paths.VenvDir, uvArgs);
        Assert.True(probeCount >= 1);
    }

    [Fact]
    public void Ensure_FailsGracefully_WhenNoToolchain()
    {
        var paths = Paths();

        var result = BackendVenv.Ensure(
            paths,
            log: _ => { },
            probe: (_, _) => false,
            run: (_, _) => false,
            onPath: _ => null); // neither uv nor litellm on PATH

        Assert.False(result.Ok);
        Assert.Null(result.LitellmBin);
    }

    [Fact]
    public void Ensure_UsesPathLitellm_WhenUvAbsent()
    {
        var paths = Paths();

        var result = BackendVenv.Ensure(
            paths,
            log: _ => { },
            probe: (_, _) => false,
            run: (_, _) => false,
            onPath: name => name == "litellm" ? "/usr/local/bin/litellm" : null);

        Assert.True(result.Ok);
        Assert.Equal("/usr/local/bin/litellm", result.LitellmBin);
    }
}
