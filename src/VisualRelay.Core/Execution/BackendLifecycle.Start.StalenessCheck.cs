using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Execution;

public sealed partial class BackendLifecycle
{
    /// <summary>
    /// When the proxy is already healthy, regenerates the config in memory and
    /// compares it against the on-disk generated file the proxy was started
    /// with. On drift it restarts the proxy with the fresh config — gated on
    /// no active run (mid-run defers with a status note instead). A matching
    /// config returns <c>false</c> so the caller keeps the fast no-op.
    /// Returns <c>true</c> when a restart was triggered (successfully or not).
    /// </summary>
    private async Task<bool> TryStalenessRestartAsync(
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        // Guard: no repo root → no template to regenerate from.
        if (_options.RepoRoot is not { } repoRoot)
            return false;

        var template = Path.Combine(repoRoot, "tools", "backend", "litellm-config.yaml");
        if (!File.Exists(template))
            return false;

        // If there is no generated config on disk, the proxy was started with the
        // static template — nothing to compare against.
        if (!File.Exists(_paths.GeneratedConfig))
            return false;

        // Guard: a run is active — defer the restart.
        if (_isRunActive?.Invoke() == true)
        {
            _log("stale backend config detected but a run is active; restart deferred");
            return false;
        }

        // Read the on-disk generated config.
        string onDisk;
        try
        {
            onDisk = await File.ReadAllTextAsync(_paths.GeneratedConfig, cancellationToken);
        }
        catch (IOException)
        {
            return false;
        }

        // Regenerate in memory — never writes to disk unless a restart is needed.
        var (freshYaml, _) = BackendConfigStep.Generate(template, repoRoot, env: _env);

        // Zero-key in fresh generation: don't destabilise a working proxy.
        if (freshYaml is null)
            return false;

        // Configs match — fast no-op.
        if (freshYaml == onDisk)
            return false;

        // Drift detected: restart the proxy with the fresh config.
        // LaunchProxyAsync → ResolveAsync will write the config + summary;
        // we only need to stop the existing proxy and hand off.
        _log("stale backend config detected; restarting proxy with fresh config");

        await StopAsync(cancellationToken, timeProvider);

        var launched = await LaunchProxyAsync(cancellationToken);
        if (launched is { } early)
            return false; // launch failed — caller sees the error

        var result = await PollReadinessAsync(cancellationToken, timeProvider);
        return result.ExitCode == 0;
    }
}
