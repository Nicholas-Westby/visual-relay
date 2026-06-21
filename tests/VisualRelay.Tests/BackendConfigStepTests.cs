using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Config-generation (<see cref="BackendConfigStep"/>) and spawned-proxy
/// bytecode-suppression tests for the ported backend lifecycle — the second
/// partial of <see cref="BackendLifecycleStatusTests"/> (sharing its temp-XDG
/// fixture). Replaces the runtime gen-config-timeout and PYTHONDONTWRITEBYTECODE
/// characterization tests that drove the retired <c>backend.sh</c>.
/// </summary>
public sealed partial class BackendLifecycleStatusTests
{
    // ── Gen-config: success writes generated config to scratch ───────────

    [Fact]
    public async Task GenConfig_Success_WritesGeneratedConfigToScratch()
    {
        var (repoRoot, _) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        var config = await BackendConfigStep.ResolveAsync(
            paths, repoRoot, TimeSpan.FromSeconds(10), _ => { });

        Assert.Equal(paths.GeneratedConfig, config);
        Assert.True(File.Exists(paths.GeneratedConfig));
        var text = await File.ReadAllTextAsync(paths.GeneratedConfig);
        Assert.Contains("model_list", text);
    }

    // ── Gen-config: timeout falls back to the static template ────────────

    [Fact]
    public async Task GenConfig_Timeout_FallsBackToStaticTemplate()
    {
        var (repoRoot, template) = WriteStaticTemplate();
        var paths = Paths();
        var log = new List<string>();

        // A zero timeout makes generation cancel before completing, exercising the
        // OperationCanceledException -> static fallback branch.
        var config = await BackendConfigStep.ResolveAsync(
            paths, repoRoot, TimeSpan.Zero, log.Add);

        Assert.Equal(template, config);
        Assert.Contains(log, l => l.Contains("timed out") && l.Contains("static config"));
    }

    [Fact]
    public async Task GenConfig_MissingTemplate_ReturnsTemplatePath_NoGeneration()
    {
        var paths = Paths();
        var repoRoot = Path.Combine(_home, "no-template-repo");
        var template = Path.Combine(repoRoot, "tools", "backend", "litellm-config.yaml");

        var config = await BackendConfigStep.ResolveAsync(
            paths, repoRoot, TimeSpan.FromSeconds(5), _ => { });

        Assert.Equal(template, config);
        Assert.False(File.Exists(paths.GeneratedConfig));
    }

    // ── PYTHONDONTWRITEBYTECODE on the spawned proxy ─────────────────────

    [Fact]
    public async Task Start_ExportsPythonDontWriteBytecode_ForSpawnedLitellm()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX shell stubs; the proxy only runs on macOS/Linux.

        var paths = Paths();
        Directory.CreateDirectory(Path.Combine(paths.VenvDir, "bin"));

        // A real python so BackendVenv's `python -V` execution probe passes, plus a
        // litellm stub that records its environment then exits. Both need an
        // executable bit; when the test runs inside a sandbox that denies chmod/
        // exec (the `check` nono profile), skip — the bytecode export is also
        // proven by the hermetic unit assertions; this is the real-spawn backstop.
        var realPython = ResolvePython();
        Assert.NotNull(realPython);
        var envLog = Path.Combine(_home, "litellm-env.txt");
        if (!TryMakeExecutable(() => CreateSymlinkOrCopy(realPython!, paths.VenvPython)) ||
            !TryMakeExecutable(() =>
            {
                File.WriteAllText(paths.VenvLitellm, $"#!/bin/bash\nenv > '{envLog}'\nexit 0\n");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(paths.VenvLitellm,
                        UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
            }))
        {
            return; // sandbox denies the chmod/exec this real-spawn check requires.
        }

        var options = new BackendStartOptions
        {
            RepoRoot = null, // skip gen-config; no template needed
            ReadyTimeout = TimeSpan.FromSeconds(3),
        };
        await Lifecycle(healthy: false, options: options).StartAsync();

        // Give the spawned stub a beat to write its env dump.
        for (var i = 0; i < 50 && !File.Exists(envLog); i++)
            await Task.Delay(50);

        Assert.True(File.Exists(envLog), "litellm stub never ran (no env captured)");
        var env = await File.ReadAllLinesAsync(envLog);
        Assert.Contains("PYTHONDONTWRITEBYTECODE=1", env);
    }

    private static bool TryMakeExecutable(Action setup)
    {
        try { setup(); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private static string? ResolvePython()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in new[] { "python3", "python" })
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }

        return null;
    }

    private static void CreateSymlinkOrCopy(string target, string link)
    {
        // A symlink to an already-executable target needs no chmod (and chmod
        // would follow the link into a read-only nix-store python and EPERM). Only
        // the copy fallback needs the executable bit set.
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            File.Copy(target, link, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(link,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        }
    }
}
