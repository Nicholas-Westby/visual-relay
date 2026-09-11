using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Centralized git process factory that pins a stable git binary at first use
/// and sanitizes the environment so nix-store churn on macOS cannot rot git
/// invocations mid-run. Resolution is cached process-wide via Lazy&lt;string&gt;;
/// the first instance pays the probe, all later ones reuse the resolved path.
/// A workspace inside a WSL distro is served by the git INSIDE that distro
/// instead (<see cref="GitRouting"/>), decided here per call, so no call site
/// ever chooses an invoker.
/// </summary>
public sealed partial class GitInvoker : IGitInvoker
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<string> CachedGitBinary = new(() =>
    {
        Interlocked.Increment(ref _probeCount);
        return ResolveGitBinary();
    });
    private static int _probeCount;
    internal static int ProbeCount => Volatile.Read(ref _probeCount);

    private readonly object _lock = new();
    private readonly WslContext? _wsl;
    private readonly Func<WslLaunch, CancellationToken, Task<(int ExitCode, string Output, bool TimedOut)>>? _runWsl;
    private string? _gitBinary;
    private IReadOnlySet<string>? _envRemove;

    /// <summary>
    /// Creates an invoker that will auto-resolve the git binary on first call.
    /// </summary>
    public GitInvoker() { }

    /// <summary>
    /// Creates an invoker pre-pinned to <paramref name="binaryPath"/>
    /// (test constructor). The env-sanitize set is computed from the binary path.
    /// </summary>
    public GitInvoker(string binaryPath)
    {
        lock (_lock)
        {
            _gitBinary = binaryPath;
            _envRemove = binaryPath.Contains("/nix/store/", StringComparison.Ordinal)
                ? null
                : new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        }
    }

    /// <summary>
    /// Test constructor: routes against <paramref name="wsl"/> and runs each
    /// wsl.exe launch through <paramref name="runWsl"/>, so no process starts.
    /// </summary>
    internal GitInvoker(
        WslContext wsl, Func<WslLaunch, CancellationToken, Task<(int ExitCode, string Output, bool TimedOut)>> runWsl)
    {
        _wsl = wsl;
        _runWsl = runWsl;
    }

    public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        var context = ContextFor(rootPath);
        if (GitRouting.Decide(rootPath, context) is GitRoute.Wsl route)
        {
            var launch = GitRouting.Launch(route, WslExe.Resolve(context), [.. arguments], environment);
            if (_runWsl is not null)
                return await _runWsl(launch, cancellationToken);

            // git gets its root through -C, so wsl.exe's own working directory
            // only has to be one it can translate; the temp directory always is.
            return await ProcessCapture.RunAsync(
                launch.FileName, launch.Arguments, Path.GetTempPath(), timeout ?? DefaultTimeout,
                cancellationToken, launch.Environment, killToken, onActivity, reapProcessTree: false);
        }

        var gitBinary = EnsureResolved();
        var sanitizedEnv = SanitizeEnvironment(environment);

        return await ProcessCapture.RunAsync(
            gitBinary,
            ["-C", rootPath, .. arguments],
            rootPath,
            timeout ?? DefaultTimeout,
            cancellationToken,
            sanitizedEnv,
            killToken,
            onActivity,
            _envRemove,
            reapProcessTree: false);
    }

    /// <summary>
    /// The WSL context a root is routed against: the injected one, else on Windows
    /// the process-wide probe, except for a Windows drive root, which Git for
    /// Windows serves and which therefore never waits on the probe (VR's own
    /// tooling on its Windows checkout stays as fast as before).
    /// </summary>
    private WslContext? ContextFor(string rootPath)
    {
        if (_wsl is not null)
            return _wsl;
        if (!OperatingSystem.IsWindows() || WslPath.TryDriveToMnt(rootPath, out _))
            return null;
        return WslContextResolver.TryGetCurrent();
    }

    /// <summary>
    /// Build a sanitized environment dictionary for the git process.
    /// When the pinned binary is outside /nix/store, DEVELOPER_DIR and
    /// SDKROOT are stripped so the xcrun shim cannot resurrect a stale
    /// nix store path.  Caller-supplied vars override inherited ones.
    /// </summary>
    private IReadOnlyDictionary<string, string> SanitizeEnvironment(
        IReadOnlyDictionary<string, string>? callerEnv)
    {
        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);

        // Inherit the current process environment.
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            var keyStr = key.ToString()!;
            var val = Environment.GetEnvironmentVariable(keyStr);
            if (val is not null)
                sanitized[keyStr] = val;
        }

        // Overlay caller-supplied vars.
        if (callerEnv is not null)
        {
            foreach (var kvp in callerEnv)
            {
                sanitized[kvp.Key] = kvp.Value;
            }
        }

        // Strip sanitized keys last — they must never reach the git
        // process regardless of inheritance or caller intent.
        if (_envRemove is not null)
        {
            foreach (var key in _envRemove)
            {
                sanitized.Remove(key);
            }
        }

        return sanitized;
    }

    private string EnsureResolved()
    {
        var binary = _gitBinary;
        if (binary is not null)
            return binary;

        lock (_lock)
        {
            if (_gitBinary is not null)
                return _gitBinary;

            _gitBinary = CachedGitBinary.Value;

            // When the pinned binary lives outside /nix/store — i.e. the
            // system git at /usr/bin/git — strip DEVELOPER_DIR / SDKROOT
            // so the xcrun shim cannot resurrect a stale nix store path
            // that was inherited from the shell environment.
            if (!_gitBinary.Contains("/nix/store/", StringComparison.Ordinal))
            {
                _envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
            }

            return _gitBinary;
        }
    }
}
