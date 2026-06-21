using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Outcome of a lifecycle subcommand: a process exit code plus the single status
/// line printed to stdout (for <c>status</c>). The exit codes match the retired
/// <c>tools/backend/backend.sh</c>: 0 = success/healthy, 1 = failure/down.
/// </summary>
public readonly record struct BackendResult(int ExitCode, string? Status)
{
    public static BackendResult Ok(string? status = null) => new(0, status);
    public static BackendResult Down(string status) => new(1, status);
}

/// <summary>
/// The single C# implementation of the local model backend (LiteLLM proxy)
/// lifecycle — <c>start</c> / <c>stop</c> / <c>status</c> — shared by the GUI
/// autostart path and the published <c>VisualRelay.Backend</c> CLI tool. This is
/// the port of <c>tools/backend/backend.sh</c>: idempotent start with a bounded
/// readiness poll on <see cref="ModelBackend.BaseUrl"/>, SIGTERM→SIGKILL stop
/// that always removes the pidfile, and a status probe. All per-machine state
/// lives under XDG (<see cref="BackendPaths"/>); spawned Python never writes
/// bytecode (<c>PYTHONDONTWRITEBYTECODE=1</c>).
/// </summary>
public sealed partial class BackendLifecycle
{
    private readonly BackendPaths _paths;
    private readonly BackendStartOptions _options;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task<bool>> _healthCheck;
    private readonly Func<BackendPaths, Action<string>, BackendVenv.Result> _ensureVenv;

    public BackendLifecycle(
        BackendPaths? paths = null,
        BackendStartOptions? options = null,
        Action<string>? log = null,
        Func<CancellationToken, Task<bool>>? healthCheck = null,
        Func<BackendPaths, Action<string>, BackendVenv.Result>? ensureVenv = null)
    {
        _paths = paths ?? BackendPaths.Resolve();
        _options = options ?? BackendStartOptions.FromEnvironment();
        // Default log sink mirrors the bash `log()` helper (prefix + stderr).
        _log = log ?? (line => Console.Error.WriteLine($"backend: {line}"));
        // Default health check reuses the app's never-throwing readiness probe
        // (the same one the GUI status dot and the pre-run guard use) so there is
        // one readiness implementation. Injectable so stop/status tests stay
        // hermetic against whatever happens to be listening on :4000.
        _healthCheck = healthCheck
            ?? (async ct => (await BackendReadinessProbe.CheckAsync(ct)).IsReady);
        // Venv provisioning is injectable so start-path tests exercise the
        // missing-toolchain and spawn branches without a real Python/uv toolchain.
        _ensureVenv = ensureVenv ?? ((p, l) => BackendVenv.Ensure(p, l));
    }

    /// <summary>
    /// Reports whether the proxy is up: a 2xx on the readiness endpoint is
    /// healthy (exit 0); a live-but-unhealthy process, a stale pidfile, or no
    /// process at all are each down (exit 1) with a descriptive line — the bash
    /// <c>cmd_status</c> contract.
    /// </summary>
    public async Task<BackendResult> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (await IsHealthyAsync(cancellationToken))
        {
            var pid = BackendProcess.ReadLivePid(_paths.PidFile);
            return BackendResult.Ok(pid is { } p
                ? $"up: healthy at {ModelBackend.BaseUrl} (pid {p})"
                : $"up: healthy at {ModelBackend.BaseUrl} (not managed by this tool)");
        }

        var livePid = BackendProcess.ReadLivePid(_paths.PidFile);
        if (livePid is { } running)
            return BackendResult.Down(
                $"down: process {running} running but {ReadinessUrl} not answering");

        if (File.Exists(_paths.PidFile))
            return BackendResult.Down($"down: stale pidfile {_paths.PidFile} (process gone)");

        return BackendResult.Down($"down: no process at {ModelBackend.BaseUrl}");
    }

    /// <summary>
    /// Stops the proxy: SIGTERM, then SIGKILL after the grace window, and ALWAYS
    /// removes the pidfile so the next start is unblocked — even when the process
    /// was already dead or the pidfile was stale. A no-op (exit 0) when nothing
    /// is running. Mirrors the bash <c>cmd_stop</c>.
    /// </summary>
    public async Task<BackendResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var pid = BackendProcess.ReadLivePid(_paths.PidFile);
        if (pid is not { } target)
        {
            if (File.Exists(_paths.PidFile))
            {
                _log($"no live process; removing stale pidfile {_paths.PidFile}");
                BackendProcess.RemovePidFile(_paths.PidFile);
            }
            else
            {
                _log("not running (no pidfile)");
            }

            return BackendResult.Ok();
        }

        _log($"stopping pid {target} (SIGTERM)");
        BackendProcess.SendTerm(target);

        await WaitForExitAsync(target, _options.StopGrace, cancellationToken);

        if (BackendProcess.IsAlive(target))
        {
            _log($"pid {target} still alive after {_options.StopGrace.TotalSeconds:F0}s; SIGKILL");
            BackendProcess.SendKill(target);
        }

        // ALWAYS clean up the pidfile, even after an abrupt prior kill / already-dead.
        BackendProcess.RemovePidFile(_paths.PidFile);
        _log("stopped; pidfile removed");
        return BackendResult.Ok();
    }

    private static string ReadinessUrl =>
        $"{ModelBackend.BaseUrl.TrimEnd('/')}{ModelBackend.ReadinessPath}";

    // A single 2xx on the readiness endpoint => a healthy proxy is already up.
    private Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        _healthCheck(cancellationToken);

    private static async Task WaitForExitAsync(int pid, TimeSpan grace, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline)
        {
            if (!BackendProcess.IsAlive(pid))
                return;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
