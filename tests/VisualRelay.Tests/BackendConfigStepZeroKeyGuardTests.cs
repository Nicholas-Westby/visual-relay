using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Zero-key guard tests for <see cref="BackendConfigStep"/> — the third partial
/// of <see cref="BackendLifecycleStatusTests"/> (sharing its temp-XDG fixture).
/// Covers the three zero-key scenarios: empty present-set with no key file,
/// key file exists but detection sees nothing, and the durable summary line on
/// successful generation.
/// </summary>
public sealed partial class BackendLifecycleStatusTests
{
    // ── Zero-key: flat empty env — falls back to static template ──────────

    [Fact]
    public async Task GenConfig_ZeroKeysNoKeyFile_FallsBackToStaticTemplate_NoGeneratedFileWritten()
    {
        var (repoRoot, template) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        // Empty env accessor — no HOME, no provider keys anywhere.
        var env = new DictionaryEnvironmentAccessor();
        var log = new List<string>();

        var config = await BackendConfigStep.ResolveAsync(
            paths, repoRoot, TimeSpan.FromSeconds(10), log.Add, env: env);

        // Must fall back to the static template, not write a degenerate generated file.
        Assert.Equal(template, config);
        Assert.Contains(log, l => l.Contains("zero keys") && l.Contains("static config"));

        // No generated config file on disk.
        Assert.False(File.Exists(paths.GeneratedConfig));
    }

    // ── Zero-key: key file present but detection saw nothing ──────────────

    [Fact]
    public async Task GenConfig_KeyFileExistsButDetectionEmpty_SurfacesAlert()
    {
        var (repoRoot, template) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        // Write a user-level .env that has provider keys.
        var configDir = Path.Combine(_home, ".config", "visual-relay");
        Directory.CreateDirectory(configDir);
        var keyFile = Path.Combine(configDir, ".env");
        await File.WriteAllTextAsync(keyFile, "HF_TOKEN=hf_secret\nDEEPSEEK_API_KEY=ds_secret\n");

        // Two-face accessor: first HOME call returns null (Read fails with
        // InvalidOperationException → present empty), subsequent calls return
        // _home (ResolvePathForCurrentUser and guard's Read succeed). Simulates
        // a transient env-resolution failure where path resolution recovered
        // between the detection phase and the file-exists check.
        var twoFace = new TwoFaceHomeAccessor(_home);
        var log = new List<string>();

        var config = await BackendConfigStep.ResolveAsync(
            paths, repoRoot, TimeSpan.FromSeconds(10), log.Add, env: twoFace);

        // Must fall back to the static template.
        Assert.Equal(template, config);
        // Must surface the WARNING alert mentioning the key file path and key names.
        Assert.Contains(log, l =>
            l.Contains("WARNING") && l.Contains(keyFile) && l.Contains("HF_TOKEN"));
        // Key values must never appear in logs.
        Assert.DoesNotContain(log, l => l.Contains("hf_secret"));
    }

    // ── Durable summary: successful generation writes summary log ────────

    [Fact]
    public async Task GenConfig_SummaryLinePersistedToGenerationSummaryLog()
    {
        var (repoRoot, _) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        // Set up a user-level .env with provider keys so generation succeeds.
        var configDir = Path.Combine(_home, ".config", "visual-relay");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, ".env"), "HF_TOKEN=hf_test\n");

        // Env accessor with HOME set so KeyEnvFile finds the .env file.
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = _home };

        var config = await BackendConfigStep.ResolveAsync(
            paths, repoRoot, TimeSpan.FromSeconds(10), _ => { }, env: env);

        // Generation must succeed and write the generated config.
        Assert.True(File.Exists(paths.GeneratedConfig));

        // The generation summary log must exist and contain:
        // - A timestamp prefix
        // - Tier→model resolutions
        // - Key names (HF_TOKEN) but NOT key values
        Assert.True(File.Exists(paths.GenerationSummaryLog));
        var summary = await File.ReadAllTextAsync(paths.GenerationSummaryLog);
        Assert.StartsWith("[", summary); // ISO 8601 timestamp
        Assert.Contains("frontier", summary);
        Assert.Contains("HF_TOKEN", summary);
        Assert.DoesNotContain("hf_test", summary); // key values must not be logged
        Assert.DoesNotContain("hf_secret", summary);
        // Summary should mention at least some tier resolutions
        Assert.Contains("→", summary);
    }

    // ── Helper: two-faced accessor ───────────────────────────────────────

    /// <summary>
    /// Two-faced accessor for testing the environment-resolution-failure alert.
    /// First call to HOME returns null (so KeyEnvFile.Read fails with
    /// InvalidOperationException and returns empty), subsequent calls return
    /// the temp home path (so ResolvePathForCurrentUser and the guard's Read
    /// succeed). This simulates a transient env glitch where path resolution
    /// recovered between the detection phase and the file-exists check.
    /// </summary>
    private sealed class TwoFaceHomeAccessor : IEnvironmentAccessor
    {
        private readonly string _home;
        private bool _homeCalled;

        public TwoFaceHomeAccessor(string home) => _home = home;

        public string? GetEnvironmentVariable(string name)
        {
            if (name == "HOME")
            {
                if (!_homeCalled)
                {
                    _homeCalled = true;
                    return null; // first call: no HOME → path resolution fails
                }
                return _home; // subsequent: HOME OK
            }
            return null;
        }
    }
}
