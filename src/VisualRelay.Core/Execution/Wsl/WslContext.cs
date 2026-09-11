namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The four facts a Windows launch needs: which wsl.exe, which distro, the
/// absolute nono path inside it (a non-login <c>--exec</c> has no profile PATH),
/// and the distro user's home (where the guard profile is placed).
/// </summary>
// ReSharper disable once NotAccessedPositionalProperty.Global — NonoPath heads the nono prefix at the launch site, which lands with the executor's WSL arm
public sealed record WslContext(string WslExePath, string Distro, string NonoPath, string DistroHome);

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
    private static readonly Lazy<WslContext?> Probed = new(ProbeOnce, LazyThreadSafetyMode.ExecutionAndPublication);
    private static WslContext? _override;

    public static WslContext? TryGetCurrent()
    {
        lock (Gate)
        {
            if (_override is not null)
                return _override;
        }

        return OperatingSystem.IsWindows() ? Probed.Value : null;
    }

    /// <summary>Sets (or with null clears) the override that <see cref="TryGetCurrent"/> returns first.</summary>
    public static void Override(WslContext? context)
    {
        lock (Gate)
        {
            _override = context;
        }
    }

    /// <summary>The context a usable probe resolves to; null for any unusable probe.</summary>
    public static WslContext? FromProbe(WslProbe probe) =>
        probe.IsUsable
            ? new WslContext(probe.WslExePath!, probe.DistroName!, probe.NonoPath!, probe.DistroHome!)
            : null;

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

    private static WslContext? ProbeOnce() =>
        FromProbe(ProbeAsync(CancellationToken.None).GetAwaiter().GetResult());

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
