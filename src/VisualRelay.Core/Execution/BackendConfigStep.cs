using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Produces the litellm config path to launch with — the port of the bash
/// <c>_gen_config_with_timeout</c> + the gen-config block in <c>cmd_start</c>.
/// It generates a key-aware config from the static template in-process (via
/// <see cref="BackendConfigGenerator"/>, the same code <c>VisualRelay.GenBackendConfig</c>
/// runs), writes it to scratch, and returns that path. Generation is bounded by a
/// timeout; on timeout or any failure it logs distinctly and falls back to the
/// static template so a wedged/absent generator never blocks startup.
/// </summary>
public static class BackendConfigStep
{
    /// <summary>
    /// Resolves the config path to launch litellm with. <paramref name="repoRoot"/>
    /// locates the static template (<c>tools/backend/litellm-config.yaml</c>); when
    /// it is missing the static template is returned unchanged (no generation).
    /// </summary>
    public static async Task<string> ResolveAsync(
        BackendPaths paths,
        string? repoRoot,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken cancellationToken = default,
        IEnvironmentAccessor? env = null)
    {
        var template = repoRoot is null
            ? null
            : Path.Combine(repoRoot, "tools", "backend", "litellm-config.yaml");

        if (template is null || !File.Exists(template))
            return template ?? string.Empty;

        // A non-positive budget can't generate anything — fall straight back to the
        // static template (deterministic; mirrors a timeout outcome).
        if (timeout <= TimeSpan.Zero)
        {
            log($"gen-backend-config timed out after {timeout.TotalSeconds:F0}s; using static config");
            return template;
        }

        try
        {
            var generated = await GenerateWithTimeoutAsync(paths, template, repoRoot, timeout, log, cancellationToken, env);
            if (generated is not null)
            {
                log($"generated key-aware config at {paths.GeneratedConfig}");
                return generated;
            }

            // Zero-key case: no generated file written; fall back to static template.
            return template;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log($"gen-backend-config timed out after {timeout.TotalSeconds:F0}s; using static config");
            return template;
        }
        catch (Exception ex)
        {
            log($"gen-backend-config unavailable ({ex.GetType().Name}); using static config");
            return template;
        }
    }

    private static async Task<string?> GenerateWithTimeoutAsync(
        BackendPaths paths,
        string template,
        string? repoRoot,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken cancellationToken,
        IEnvironmentAccessor? env)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        // Generation is CPU-bound file work; run it on a pool thread so the
        // CancelAfter deadline can abandon a pathological template parse.
        var token = cts.Token;
        var (yaml, summary) = await Task.Run(() => Generate(template, repoRoot, env, log), token);

        Directory.CreateDirectory(paths.Scratch);

        if (yaml is null)
        {
            // Zero-key or environment-resolution failure: no generated config file.
            return null;
        }

        await File.WriteAllTextAsync(paths.GeneratedConfig, yaml, token);

        // Persist the durable generation summary.
        var line = $"[{DateTime.UtcNow:O}] {summary}{Environment.NewLine}";
        await File.AppendAllTextAsync(paths.GenerationSummaryLog, line, token);

        return paths.GeneratedConfig;
    }

    /// <summary>
    /// Generates a key-aware config from the static template. Returns
    /// (<c>null</c>, summary) when the provider-key set is empty (zero-key
    /// guard) so callers fall back to the static template — the same pattern
    /// used for generation timeout/failure.
    /// </summary>
    internal static (string? Yaml, string Summary) Generate(
        string template,
        string? repoRoot,
        IEnvironmentAccessor? env = null,
        Action<string>? log = null)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        string? keyFile = null;
        Dictionary<string, string> fileKeys;

        // First attempt: read from user-level .env through the accessor seam.
        // May fail with InvalidOperationException when HOME is unavailable
        // (transient env-resolution glitch). We re-probe below when needed.
        try
        {
            keyFile = KeyEnvFile.ResolvePathForCurrentUser(env);
            fileKeys = File.Exists(keyFile) ? KeyEnvFile.Read(keyFile) : [];
            foreach (var (key, _) in fileKeys)
                present.Add(key);
        }
        catch (InvalidOperationException)
        {
            fileKeys = [];
        }

        // Process env: check the five known provider-key vars through the accessor.
        foreach (var k in new[] { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY" })
            if (KeyEnvFile.GetEnv(k, env) is not null)
                present.Add(k);

        // Zero-key guard: no provider keys detected.
        if (present.Count == 0)
        {
            // Second attempt: the env may have recovered since the first probe
            // (transient HOME/XDG_CONFIG_HOME glitch). Re-resolve the key-file
            // path and check whether the file actually has keys.
            if (fileKeys.Count == 0)
            {
                try
                {
                    keyFile = KeyEnvFile.ResolvePathForCurrentUser(env);
                    fileKeys = File.Exists(keyFile) ? KeyEnvFile.Read(keyFile) : [];
                }
                catch (InvalidOperationException)
                {
                    // Still unreachable — treat as truly no keys.
                }
            }

            // Distinguish "file exists with keys but detection saw nothing"
            // from "no key file at all".
            if (fileKeys.Count > 0)
            {
                var keyNames = string.Join(", ", fileKeys.Keys.OrderBy(k => k, StringComparer.Ordinal));
                var msg = $"WARNING: provider key file {keyFile} contains " +
                          $"{fileKeys.Count} key(s) ({keyNames}) but detection " +
                          $"saw zero keys — environment-resolution failure; using static config";
                log?.Invoke(msg);
                return (null, $"backend: zero keys — env-resolution failure; using static config");
            }

            log?.Invoke("gen-backend-config: zero keys detected; using static config");
            return (null, "backend: zero keys — using static config");
        }

        // Load tier model overrides from .relay/config.json.
        IReadOnlyDictionary<string, string>? overrides = null;
        if (repoRoot is not null)
        {
            var configResult = RelayConfigLoader.TryLoadAsync(repoRoot).GetAwaiter().GetResult();
            if (configResult.Status == RelayConfigStatus.Loaded)
                overrides = configResult.Config.TierModelOverrides;
        }

        return BackendConfigGenerator.Generate(present, template, overrides);
    }
}
