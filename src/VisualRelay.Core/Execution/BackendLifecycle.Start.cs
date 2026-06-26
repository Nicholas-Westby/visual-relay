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

    // Spawns the proxy detached, with the child redirecting its own stdout+stderr to
    // the log FILE through the OS shell (`>LOG 2>&1`, truncating). Detaching through a
    // file (no pipe back to this tool) is why the proxy keeps serving after the
    // one-shot `start` exits at readiness — on every OS. Returns the launched pid.
    private int SpawnLitellm(string litellmBin, string config, IReadOnlyDictionary<string, string> env)
    {
        var host = new Uri(ModelBackend.BaseUrl).Host;
        var port = new Uri(ModelBackend.BaseUrl).Port.ToString();

        var psi = BuildBackendStartInfo(
            litellmBin, _paths.VenvUvicorn, config, host, port, _paths.LogFile, env, OperatingSystem.IsWindows());
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the model backend");
        return process.Id;
    }

    // Builds the ProcessStartInfo that launches the proxy through the OS shell so the
    // CHILD redirects its own stdout+stderr to the log FILE (`>LOG 2>&1`, which
    // truncates on each start) and fully detaches — no pipe back to this tool, so the
    // proxy survives the one-shot `start` exiting at readiness. On Windows litellm's
    // CLI worker crashes silently, so the proxy ASGI app runs via uvicorn directly
    // with the config in CONFIG_FILE_PATH; Unix runs the litellm CLI under `exec` so
    // the captured pid IS the proxy's. PYTHONDONTWRITEBYTECODE keeps Python from
    // writing bytecode into a (often system-owned) stdlib dir.
    internal static ProcessStartInfo BuildBackendStartInfo(
        string litellmBin, string uvicornBin, string config, string host, string port,
        string logFile, IReadOnlyDictionary<string, string> env, bool isWindows)
    {
        ProcessStartInfo psi;
        if (isWindows)
        {
            // Raw argument string: cmd.exe parses its own command line, so the
            // redirect and quoted paths are not re-escaped by .NET argv quoting. The
            // whole command is wrapped in ONE outer quote pair — `cmd /c "…"` strips
            // exactly that pair, leaving the inner quoted paths intact (without the
            // wrap, cmd's multi-quote rule strips the uvicorn path's own quotes).
            var inner = $"{WinQuote(uvicornBin)} litellm.proxy.proxy_server:app " +
                        $"--host {host} --port {port} > {WinQuote(logFile)} 2>&1";
            psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"{inner}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(config))
                psi.Environment["CONFIG_FILE_PATH"] = config;
        }
        else
        {
            var configArg = string.IsNullOrEmpty(config) ? string.Empty : $"--config {ShQuote(config)} ";
            psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                $"exec {ShQuote(litellmBin)} {configArg}--host {ShQuote(host)} --port {port} " +
                $">{ShQuote(logFile)} 2>&1");
        }

        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        psi.Environment["DISABLE_AIOHTTP_TRANSPORT"] = Default("DISABLE_AIOHTTP_TRANSPORT", "True");
        psi.Environment["LITELLM_MAX_STREAMING_DURATION_SECONDS"] =
            Default("LITELLM_MAX_STREAMING_DURATION_SECONDS", "240");
        foreach (var (k, v) in env)
            psi.Environment[k] = v;
        return psi;
    }

    // Single-quote a value for POSIX sh, escaping embedded single quotes (the '\'' idiom).
    private static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    // Double-quote a path for cmd.exe so a space-containing path stays one token.
    private static string WinQuote(string value) => "\"" + value + "\"";

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
