using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Staleness-check tests for <see cref="BackendLifecycle"/> — the fourth partial
/// of <see cref="BackendLifecycleStatusTests"/> (sharing its temp-XDG fixture).
/// Covers the healthy-proxy config comparison: stale config triggers a restart,
/// matching config keeps the fast no-op, and an active run defers the restart.
/// </summary>
public sealed partial class BackendLifecycleStatusTests
{
    // ── Healthy proxy, stale on-disk config → restart with fresh config ──

    [Fact]
    public async Task Start_HealthyStaleConfigOnDisk_RestartsWithFreshConfig()
    {
        var (repoRoot, _) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        // Set up keys so fresh generation produces a real config.
        var configDir = Path.Combine(_home, ".config", "visual-relay");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, ".env"),
            "HF_TOKEN=hf_test\nDEEPSEEK_API_KEY=ds_test\nMOONSHOT_API_KEY=ms_test\n");

        // Write a STALE degenerate config to disk (all tiers → fallback).
        var staleYaml = """
model_list:
  - model_name: fallback
    litellm_params:
      model: huggingface/mistralai/Mistral-Nemo-12B-Instruct
      api_base: https://api-inference.huggingface.co/models/mistralai/Mistral-Nemo-12B-Instruct/v1
      api_key: os.environ/HF_TOKEN

litellm_settings:
  model_group_alias:
    frontier: fallback
    balanced: fallback
    cheap: fallback
    fallback: fallback
  num_retries: 5
  fallbacks:
    - frontier: [fallback]
    - balanced: [fallback]
    - cheap: [fallback]
    - fallback: [fallback]
  set_verbose: true

general_settings:
  master_key: os.environ/LITELLM_MASTER_KEY
  database_url: os.environ/LITELLM_DATABASE_URL
""";

        await File.WriteAllTextAsync(paths.GeneratedConfig, staleYaml);
        Assert.True(File.Exists(paths.GeneratedConfig)); // pre-existing stale file

        var log = new List<string>();
        var spawned = false;
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = _home };
        var options = new BackendStartOptions
        {
            RepoRoot = repoRoot,
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };

        var lifecycle = new BackendLifecycle(
            paths,
            options,
            log.Add,
            healthCheck: _ => Task.FromResult(true), // proxy appears healthy
            ensureVenv: (_, _) =>
            {
                spawned = true;
                return new BackendVenv.Result(null); // restart fails venv, but proves path was taken
            },
            env: env,
            isRunActive: () => false); // idle — restart allowed

        var time = new ManualTimeProvider();
        var task = lifecycle.StartAsync(timeProvider: time);
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        await task;

        // The staleness check should have attempted a restart (spawn) because
        // the degenerative on-disk config differs from fresh generation.
        // Even though the restart fails (no toolchain in hermetic test), the
        // important thing is it tried.
        Assert.True(spawned, "Expected a restart to be triggered for stale config");
        Assert.Contains(log, l => l.Contains("stale") && l.Contains("config"));
    }

    // ── Healthy proxy, matching config → fast no-op ──────────────────────

    [Fact]
    public async Task Start_HealthyMatchingConfig_NoRestart_FastNoOp()
    {
        var (repoRoot, _) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        // Set up keys so generation works.
        var configDir = Path.Combine(_home, ".config", "visual-relay");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, ".env"),
            "HF_TOKEN=hf_test\nDEEPSEEK_API_KEY=ds_test\nMOONSHOT_API_KEY=ms_test\n");

        // Write a CORRECT config to disk — matching what fresh generation produces.
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = _home };
        var (freshYaml, _) = BackendConfigStep.Generate(
            Path.Combine(repoRoot, "tools", "backend", "litellm-config.yaml"),
            repoRoot, env);
        Assert.NotNull(freshYaml); // generation succeeded with keys
        await File.WriteAllTextAsync(paths.GeneratedConfig, freshYaml!);

        var log = new List<string>();
        var spawned = false;
        var options = new BackendStartOptions
        {
            RepoRoot = repoRoot,
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };

        var lifecycle = new BackendLifecycle(
            paths,
            options,
            log.Add,
            healthCheck: _ => Task.FromResult(true), // proxy appears healthy
            ensureVenv: (_, _) =>
            {
                spawned = true;
                return new BackendVenv.Result(null);
            },
            env: env,
            isRunActive: () => false);

        var result = await lifecycle.StartAsync();

        // Configs match → fast no-op, no restart.
        Assert.Equal(0, result.ExitCode);
        Assert.False(spawned, "Expected no restart when configs match");
        Assert.Contains(log, l => l.Contains("already healthy"));
    }

    // ── Healthy proxy, stale config, mid-run → defer with status note ────

    [Fact]
    public async Task Start_HealthyStaleConfig_MidRun_DefersWithStatusNote()
    {
        var (repoRoot, _) = WriteStaticTemplate();
        var paths = Paths();
        Directory.CreateDirectory(paths.Scratch);

        // Set up keys.
        var configDir = Path.Combine(_home, ".config", "visual-relay");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, ".env"),
            "HF_TOKEN=hf_test\nDEEPSEEK_API_KEY=ds_test\nMOONSHOT_API_KEY=ms_test\n");

        // Write a stale degenerate config.
        await File.WriteAllTextAsync(paths.GeneratedConfig, "stale: true\n");

        var log = new List<string>();
        var spawned = false;
        var env = new DictionaryEnvironmentAccessor { ["HOME"] = _home };
        var options = new BackendStartOptions
        {
            RepoRoot = repoRoot,
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };

        var lifecycle = new BackendLifecycle(
            paths,
            options,
            log.Add,
            healthCheck: _ => Task.FromResult(true), // proxy appears healthy
            ensureVenv: (_, _) =>
            {
                spawned = true;
                return new BackendVenv.Result(null);
            },
            env: env,
            isRunActive: () => true); // a run is active — restart must be deferred

        var result = await lifecycle.StartAsync();

        // No restart triggered — run is active.
        Assert.Equal(0, result.ExitCode);
        Assert.False(spawned, "Expected no restart while a run is active");
        Assert.Contains(log, l => l.Contains("run") && l.Contains("active") && l.Contains("defer"));
    }
}
