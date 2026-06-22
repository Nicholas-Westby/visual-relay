using System.Diagnostics;
using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class BackendLifecycle
{
    /// <summary>
    /// Starts the proxy idempotently: a healthy instance is a no-op (exit 0);
    /// otherwise it clears a stale pidfile, provisions/self-heals the venv, loads
    /// provider keys (process env wins), resolves a key-aware config (bounded,
    /// static fallback), spawns litellm with <c>PYTHONDONTWRITEBYTECODE=1</c>, and
    /// polls readiness up to the bounded timeout — failing fast with a log tail if
    /// the process dies while booting. Degrades with a clear message + exit 1 when
    /// the litellm toolchain is missing. The port of the bash <c>cmd_start</c>.
    /// </summary>
    public async Task<BackendResult> StartAsync(CancellationToken cancellationToken = default)
    {
        CleanLegacyRepoState();
        Directory.CreateDirectory(_paths.Scratch);

        if (await IsHealthyAsync(cancellationToken))
        {
            _log($"already healthy at {ModelBackend.BaseUrl} (no-op)");
            return BackendResult.Ok();
        }

        var existing = BackendProcess.ReadLivePid(_paths.PidFile);
        if (existing is { } booting)
        {
            _log($"process {booting} is running but not yet healthy; waiting for readiness");
        }
        else
        {
            var launched = await LaunchProxyAsync(cancellationToken);
            if (launched is { } early)
                return early;
        }

        return await PollReadinessAsync(cancellationToken);
    }

    // Provisions, configures, and spawns litellm. Returns a non-null result only
    // on an early terminal outcome (missing toolchain); null means "spawned, now
    // poll readiness".
    private async Task<BackendResult?> LaunchProxyAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_paths.PidFile))
        {
            _log($"removing stale pidfile {_paths.PidFile}");
            BackendProcess.RemovePidFile(_paths.PidFile);
        }

        var venv = _ensureVenv(_paths, _log);
        if (!venv.Ok)
        {
            LogMissingToolchain();
            return BackendResult.Down(
                $"could not start the model backend (litellm) on {ModelBackend.BaseUrl}");
        }

        var env = LoadProviderKeys();
        var config = await BackendConfigStep.ResolveAsync(
            _paths, _options.RepoRoot, _options.GenConfigTimeout, _log, cancellationToken);

        _log($"starting litellm proxy on {ModelBackend.BaseUrl} (logs: {_paths.LogFile})");
        var pid = SpawnLitellm(venv.LitellmBin!, config, env);
        await File.WriteAllTextAsync(_paths.PidFile, pid.ToString(), cancellationToken);
        _log($"pid {pid} recorded at {_paths.PidFile}");
        return null;
    }

    // Spawns litellm detached (outlives this process), redirecting stdout+stderr
    // to the log file, and returns its pid. PYTHONDONTWRITEBYTECODE=1 keeps the
    // proxy's Python from writing __pycache__/*.pyc into a (often system-owned)
    // stdlib dir — the same suppression the nono-wrapped paths apply.
    private int SpawnLitellm(string litellmBin, string config, IReadOnlyDictionary<string, string> env)
    {
        var host = new Uri(ModelBackend.BaseUrl).Host;
        var port = new Uri(ModelBackend.BaseUrl).Port.ToString();

        // Spawn through `sh -c 'exec litellm … >LOG 2>&1'` so the log redirect to a
        // FILE happens in the child (the bash `nohup … >LOG 2>&1 &` shape) and the
        // proxy fully detaches: it has no stdio pipe back to this tool, so it keeps
        // running unharmed after the tool exits at readiness. `exec` replaces the
        // shell with litellm, so the captured pid IS the proxy's.
        var configArg = string.IsNullOrEmpty(config) ? string.Empty : $"--config {Quote(config)} ";
        var shellCommand =
            $"exec {Quote(litellmBin)} {configArg}--host {Quote(host)} --port {port} " +
            $">{Quote(_paths.LogFile)} 2>&1";

        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(shellCommand);

        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        psi.Environment["DISABLE_AIOHTTP_TRANSPORT"] = Default("DISABLE_AIOHTTP_TRANSPORT", "True");
        psi.Environment["LITELLM_MAX_STREAMING_DURATION_SECONDS"] =
            Default("LITELLM_MAX_STREAMING_DURATION_SECONDS", "240");
        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {litellmBin}");
        return process.Id;
    }

    // Single-quote a value for safe inclusion in the `sh -c` command line,
    // escaping any embedded single quotes (POSIX '\'' idiom).
    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private async Task<BackendResult> PollReadinessAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.ReadyTimeout;
        var waited = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync(cancellationToken))
            {
                _log($"ready at {ModelBackend.BaseUrl} after {waited}s");
                return BackendResult.Ok();
            }

            // If a pid was recorded but its process is gone, litellm died while
            // booting — fail fast with the log tail (the bash `live_pid` empty
            // check). A missing pidfile here means the "already booting" path with
            // no recorded pid; keep polling until the readiness timeout.
            if (File.Exists(_paths.PidFile) && BackendProcess.ReadLivePid(_paths.PidFile) is null)
            {
                _log("litellm exited before becoming ready; see the log tail:");
                LogTail();
                BackendProcess.RemovePidFile(_paths.PidFile);
                return BackendResult.Down($"litellm exited before readiness at {ModelBackend.BaseUrl}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            waited++;
        }

        _log($"timed out after {_options.ReadyTimeout.TotalSeconds:F0}s waiting for {ReadinessUrl}; see {_paths.LogFile}");
        return BackendResult.Down($"timed out waiting for readiness at {ModelBackend.BaseUrl}");
    }

    // Loads provider keys with process-env-wins precedence (the process env is
    // already inherited by the spawned child, so we only return the file-sourced
    // additions): user-level ~/.config/visual-relay/.env only. Reuses
    // KeyEnvFile so the parsing matches the GUI/settings panel.
    private Dictionary<string, string> LoadProviderKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        var userEnv = KeyEnvFile.ResolvePathForCurrentUser();
        if (File.Exists(userEnv))
        {
            _log($"loading provider keys from {userEnv}");
            Merge(keys, KeyEnvFile.GetUnsetKeysPublic(userEnv));
        }

        return keys;
    }

    private static void Merge(Dictionary<string, string> into, IReadOnlyDictionary<string, string> from)
    {
        foreach (var (k, v) in from)
            into.TryAdd(k, v);
    }

    private static string Default(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    // Removes legacy repo-local state from old checkouts (both gitignored);
    // whichever environment runs next re-provisions into its own XDG home.
    private void CleanLegacyRepoState()
    {
        if (_options.RepoRoot is not { } root)
            return;

        var legacyVenv = Path.Combine(root, "tools", "backend", ".venv");
        if (Directory.Exists(legacyVenv))
        {
            _log($"removing legacy repo-local venv at {legacyVenv}");
            TryDeleteDir(legacyVenv);
        }

        var legacyScratch = Path.Combine(root, ".relay-scratch");
        if (Directory.Exists(legacyScratch))
        {
            _log($"removing legacy repo-local scratch at {legacyScratch}");
            TryDeleteDir(legacyScratch);
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    private void LogTail()
    {
        try
        {
            var lines = File.ReadLines(_paths.LogFile).TakeLast(20);
            foreach (var line in lines)
                _log(line);
        }
        catch (IOException)
        {
            // No log to tail.
        }
    }

    private void LogMissingToolchain()
    {
        _log($"could not start the model backend (litellm) on {ModelBackend.BaseUrl}.");
        _log($"  Launch normally provisions litellm into {_paths.VenvDir} using uv. To enable it:");
        _log("    1. Install uv:  curl -LsSf https://astral.sh/uv/install.sh | sh");
        _log("    2. Provide provider keys in ~/.config/visual-relay/.env (see .env.example).");
        _log("    3. Start it again:  visual-relay launch (or VisualRelay.Backend start).");
        _log("Visual Relay still launches without the backend; the in-app probe flags it.");
    }
}
