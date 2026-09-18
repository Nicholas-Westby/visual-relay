namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The facts a Windows launch needs: which wsl.exe, which distro, the absolute
/// nono path inside it (a non-login <c>--exec</c> has no profile PATH), the distro
/// user's home (where the guard profile is placed), and the PATH their login shell
/// builds, which every launch carries so profile-installed toolchains resolve.
/// </summary>
// ReSharper disable once NotAccessedPositionalProperty.Global — NonoPath heads the nono prefix at the launch site, which lands with the executor's WSL arm
public sealed record WslContext(string WslExePath, string Distro, string NonoPath, string DistroHome, string? UserPath = null);

/// <summary>
/// Process-wide resolution of the <see cref="WslContext"/>. The override wins when
/// set (the CLI gate sets it from its own successful probe; tests set it to run
/// the Windows arm anywhere); otherwise there is no context off Windows, and on
/// Windows the machine is probed once through the real runner and the result is
/// kept for the life of the process, usable probes only.
/// </summary>
public static class WslContextResolver
{
    private static readonly TimeSpan ProbeStepTimeout = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static Func<CancellationToken, Task<WslProbe>> _prober = ProbeAsync;
    private static Lazy<Task<WslContext?>> _probed = NewProbe();
    private static WslContext? _override;

    // The one probe, memoized as a TASK both accessors share. It is started on the
    // thread pool because the blocking accessor is reachable from the UI thread:
    // probing inline would make the UI thread the continuation its own awaits are
    // queued to, and the wait would never end.
    private static Lazy<Task<WslContext?>> NewProbe() =>
        new(
            () => Task.Run(async () => FromProbe(await _prober(CancellationToken.None).ConfigureAwait(false))),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The memo, read under the same lock that replaces it. The field was written under
    /// <c>Gate</c> and read OUTSIDE it at every call site, including
    /// <see cref="TryGetCurrent"/> and <see cref="TryGetCurrentAsync"/>, which are how
    /// the running application resolves its sandbox host. A caller could therefore
    /// resolve a stale Lazy and run a probe the memo was supposed to have answered.
    /// <para>
    /// Nothing in the application swaps the prober, so its window is much narrower than
    /// the suite's — narrower, not absent, and the unsynchronised read is a defect on
    /// its own terms without needing a sighting. Where it WAS seen is the suite, where
    /// a fixture does swap it: the probe test failed taking 468 ms against a 9 ms
    /// baseline, which is the duration of a real six-step wsl.exe probe rather than of
    /// a memo read. That duration is what identified this; the assertion text said
    /// nothing. Whether it explains that one sighting is unproven — a memory-visibility
    /// race resists being made deterministic, and it was not reproducible on macOS.
    /// </para>
    /// </summary>
    private static Lazy<Task<WslContext?>> Probed
    {
        get { lock (Gate) { return _probed; } }
    }

    /// <summary>
    /// The resolved context, blocking on the one probe when it is still running.
    /// Prefer <see cref="TryGetCurrentAsync"/> anywhere an await is possible.
    /// </summary>
    public static WslContext? TryGetCurrent()
    {
        lock (Gate)
        {
            if (_override is not null)
                return _override;
        }

        return OperatingSystem.IsWindows() ? Probed.Value.GetAwaiter().GetResult() : null;
    }

    /// <summary>
    /// The resolved context without blocking: the same override and the same single
    /// probe, awaited instead of waited on. This is what a UI-thread caller uses.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait, never the shared probe.</param>
    /// <returns>The context, or null when there is none.</returns>
    public static async Task<WslContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (_override is not null)
                return _override;
        }

        if (!OperatingSystem.IsWindows())
            return null;

        return await Probed.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a scripted prober and discards the memo, or restores the real one.
    /// The probe runs a real wsl.exe and only on Windows, so the memoization itself
    /// is otherwise unobservable from a test.
    /// </summary>
    /// <param name="prober">The prober to run, or null to restore the real probe.</param>
    internal static void UseProberForTests(Func<CancellationToken, Task<WslProbe>>? prober)
    {
        lock (Gate)
        {
            _prober = prober ?? ProbeAsync;
            _probed = NewProbe();
        }
    }

    /// <summary>The memoized probe task, platform checks and override skipped.</summary>
    internal static Task<WslContext?> ProbedForTestsAsync() => Probed.Value;

    /// <summary>Sets (or with null clears) the override that <see cref="TryGetCurrent"/> returns first.</summary>
    public static void Override(WslContext? context)
    {
        lock (Gate)
        {
            _override = context;
        }
    }

    /// <summary>
    /// The last probe this resolver ran, kept beside the context so a refusal can say
    /// WHICH check failed. Without it every Windows surface could only report "not
    /// installed or not on PATH", which names neither the distro nor the fix.
    /// </summary>
    public static WslProbe? LastProbe
    {
        get { lock (Gate) { return _lastProbe; } }
    }

    /// <summary>
    /// The probe that explains a host with NO resolved distro. That is the last one
    /// this resolver ran, unless it reports a usable distro: a usable probe cannot
    /// explain a missing context (and in a test process is simply somebody else's
    /// probe), so the empty one stands in and the gate still names a real first
    /// failing check rather than deciding there is nothing wrong.
    /// </summary>
    public static WslProbe UnusableProbe =>
        LastProbe is { IsUsable: false } probe ? probe : WslProbe.Empty;

    private static WslProbe? _lastProbe;

    /// <summary>The context a usable probe resolves to; null for any unusable probe.</summary>
    public static WslContext? FromProbe(WslProbe probe)
    {
        lock (Gate)
        {
            _lastProbe = probe;
        }

        return probe.IsUsable
            ? new WslContext(probe.WslExePath!, probe.DistroName!, probe.NonoPath!, probe.DistroHome!, probe.UserPath)
            : null;
    }

    /// <summary>
    /// Probes the real machine: wsl.exe from PATH or the Windows system directory,
    /// the distro <c>VR_WSL_DISTRO</c> names, each step run as a wsl.exe child with
    /// <see cref="WslExeEnvironment.Variables"/>. The one place a real process runs.
    /// </summary>
    public static Task<WslProbe> ProbeAsync(CancellationToken ct)
    {
        var wslExe = FindWslExe();
        var requested = Environment.GetEnvironmentVariable(WslProber.DistroEnvVar);
        return WslProber.ProbeAsync(
            (argv, token) => RunWslExeAsync(wslExe!, argv, token),
            string.IsNullOrWhiteSpace(requested) ? null : requested.Trim(),
            wslExe,
            ct);
    }

    private static string? FindWslExe()
    {
        var onPath = PathExecutables.Find("wsl.exe");
        if (onPath is not null)
            return onPath;
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        return File.Exists(system) ? system : null;
    }

    private static async Task<(int ExitCode, string Output)> RunWslExeAsync(
        string wslExe, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            wslExe, argv, Path.GetTempPath(), ProbeStepTimeout, ct, environment: WslExeEnvironment.Variables);
        return (timedOut ? -1 : exitCode, output);
    }
}
