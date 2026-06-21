using VisualRelay.Core.Configuration;

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
        CancellationToken cancellationToken = default)
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
            var generated = await GenerateWithTimeoutAsync(paths, template, timeout, cancellationToken);
            log($"generated key-aware config at {paths.GeneratedConfig}");
            return generated;
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

    private static async Task<string> GenerateWithTimeoutAsync(
        BackendPaths paths,
        string template,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        // Generation is CPU-bound file work; run it on a pool thread so the
        // CancelAfter deadline can abandon a pathological template parse.
        var token = cts.Token;
        var yaml = await Task.Run(() => Generate(template), token);

        Directory.CreateDirectory(paths.Scratch);
        await File.WriteAllTextAsync(paths.GeneratedConfig, yaml, token);
        return paths.GeneratedConfig;
    }

    private static string Generate(string template)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, _) in KeyEnvFile.Read())
            present.Add(key);
        foreach (var key in new[] { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY" })
            if (Environment.GetEnvironmentVariable(key) is not null)
                present.Add(key);

        var (yaml, _) = BackendConfigGenerator.Generate(present, template);
        return yaml;
    }
}
