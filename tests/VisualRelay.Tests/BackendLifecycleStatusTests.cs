using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Status / stop decision tests plus the start-path guarantees (legacy cleanup,
/// gen-config timeout/fallback, generated-config write) for the ported C# backend
/// lifecycle. The readiness probe is injected so these stay hermetic regardless
/// of what is listening on :4000. Replaces the runtime <c>Installer5BackendSh*</c>
/// characterization tests.
/// </summary>
public sealed partial class BackendLifecycleStatusTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "vr-backend-status", Guid.NewGuid().ToString("N"));

    public BackendLifecycleStatusTests() => Directory.CreateDirectory(_home);

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

    private BackendLifecycle Lifecycle(
        bool healthy,
        BackendStartOptions? options = null,
        List<string>? log = null)
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);
        return new BackendLifecycle(
            paths,
            options ?? new BackendStartOptions(),
            log is null ? _ => { }
        : log.Add,
            healthCheck: _ => Task.FromResult(healthy));
    }

    // ── Status ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_Healthy_Exit0()
    {
        var result = await Lifecycle(healthy: true).StatusAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("up: healthy", result.Status);
    }

    [Fact]
    public async Task Status_NoProcess_Exit1()
    {
        var result = await Lifecycle(healthy: false).StatusAsync();
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no process", result.Status);
    }

    [Fact]
    public async Task Status_StalePidfile_ReportsStale()
    {
        var lifecycle = Lifecycle(healthy: false);
        // A pidfile whose pid is gone => stale.
        File.WriteAllText(Paths().PidFile, "2000000000");

        var result = await lifecycle.StatusAsync();
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stale pidfile", result.Status);
    }

    // ── Stop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_NoPidfile_NoOp_Exit0()
    {
        var log = new List<string>();
        var lifecycle = Lifecycle(healthy: false, log: log);

        var result = await lifecycle.StopAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(log, l => l.Contains("not running"));
    }

    [Fact]
    public async Task Stop_StalePidfile_RemovesIt_Exit0()
    {
        var log = new List<string>();
        var lifecycle = Lifecycle(healthy: false, log: log);
        var pidFile = Paths().PidFile;
        File.WriteAllText(pidFile, "2000000000"); // gone process => stale

        var result = await lifecycle.StopAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(pidFile)); // ALWAYS removed
        Assert.Contains(log, l => l.Contains("stale pidfile"));
    }

    // ── Legacy cleanup (start path) ──────────────────────────────────────

    [Fact]
    public async Task Start_RemovesLegacyRepoLocalState()
    {
        var repoRoot = Path.Combine(_home, "repo");
        var legacyVenv = Path.Combine(repoRoot, "tools", "backend", ".venv");
        var legacyScratch = Path.Combine(repoRoot, ".relay-scratch");
        Directory.CreateDirectory(legacyVenv);
        Directory.CreateDirectory(legacyScratch);
        await File.WriteAllTextAsync(Path.Combine(legacyVenv, "legacy.txt"), "x");

        var log = new List<string>();
        var options = new BackendStartOptions
        {
            RepoRoot = repoRoot,
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };
        // Unhealthy + no toolchain (HOME-only env, empty PATH lookups via the real
        // venv probe which fails) => start returns down quickly, but legacy
        // cleanup runs first.
        var lifecycle = Lifecycle(healthy: false, options: options, log: log);
        await lifecycle.StartAsync();

        Assert.False(Directory.Exists(legacyVenv));
        Assert.False(Directory.Exists(legacyScratch));
        Assert.Contains(log, l => l.Contains("legacy repo-local venv"));
        Assert.Contains(log, l => l.Contains("legacy repo-local scratch"));
    }

    // ── Missing toolchain: graceful degrade ──────────────────────────────

    [Fact]
    public async Task Start_MissingToolchain_LogsRemediation_Exit1()
    {
        var log = new List<string>();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);
        var lifecycle = new BackendLifecycle(
            paths,
            new BackendStartOptions { ReadyTimeout = TimeSpan.FromMilliseconds(50) },
            log.Add,
            healthCheck: _ => Task.FromResult(false),
            ensureVenv: (_, _) => new BackendVenv.Result(null)); // no toolchain

        var result = await lifecycle.StartAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(log, l => l.Contains("could not start the model backend"));
        Assert.Contains(log, l => l.Contains("uv")); // remediation mentions uv
        Assert.False(File.Exists(paths.PidFile)); // never recorded a pid
    }

    // ── Idempotent start: already healthy is a no-op ─────────────────────

    [Fact]
    public async Task Start_AlreadyHealthy_NoOp_Exit0()
    {
        var log = new List<string>();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);
        var spawned = false;
        var lifecycle = new BackendLifecycle(
            paths,
            new BackendStartOptions(),
            log.Add,
            healthCheck: _ => Task.FromResult(true), // already up
            ensureVenv: (_, _) => { spawned = true; return new BackendVenv.Result(null); });

        var result = await lifecycle.StartAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.False(spawned); // never reached venv/spawn
        Assert.Contains(log, l => l.Contains("already healthy"));
    }

    // Copies the repo's real static litellm template into a temp repo so the
    // in-process generator (BackendConfigGenerator) has its required sections.
    private (string RepoRoot, string Template) WriteStaticTemplate()
    {
        var repoRoot = Path.Combine(_home, "repo-tpl");
        var dir = Path.Combine(repoRoot, "tools", "backend");
        Directory.CreateDirectory(dir);
        var template = Path.Combine(dir, "litellm-config.yaml");
        var source = Path.Combine(RepoSetup.Root, "tools", "backend", "litellm-config.yaml");
        File.Copy(source, template, overwrite: true);
        return (repoRoot, template);
    }
}
