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
/// Resolution of the <see cref="WslContext"/>. The override wins when set (the CLI
/// gate sets it from its own successful probe); otherwise there is no context off
/// Windows, and on Windows the machine is probed once through the real runner and
/// the result is kept for the life of the process, usable probes only.
/// <para>
/// The application has exactly one such resolution. A test that needs a different
/// one opens its own with <see cref="IsolateForTests"/>, which only the async flow
/// that opened it can see. Tests used to write the process-wide one instead, and a
/// parallel run let one test's "no distro" reach another test's refusal: six Windows
/// facts failed in 12 ms each on 2026-09-18 because of it. Tests cannot call
/// <see cref="Override"/> (the test project's BannedSymbols.txt), so in a test process
/// the process-wide resolution only ever holds this machine's real answer, which
/// every test reading it would get anyway.
/// </para>
/// </summary>
public static partial class WslContextResolver
{
    private static readonly TimeSpan ProbeStepTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The application's resolution: the real probe, run at most once per process.</summary>
    private static readonly Resolution Process = new(ProbeAsync, null);

    /// <summary>A test's own resolution, set only by <see cref="IsolateForTests"/>.</summary>
    private static readonly AsyncLocal<Resolution?> Isolated = new();

    private static Resolution Current => Isolated.Value ?? Process;

    /// <summary>
    /// The resolved context, blocking on the one probe when it is still running.
    /// Prefer <see cref="TryGetCurrentAsync"/> anywhere an await is possible.
    /// </summary>
    public static WslContext? TryGetCurrent()
    {
        var resolution = Current;
        if (resolution.OverrideContext is { } context)
            return context;

        return OperatingSystem.IsWindows() ? resolution.Probed.Value.GetAwaiter().GetResult() : null;
    }

    /// <summary>
    /// The resolved context without blocking: the same override and the same single
    /// probe, awaited instead of waited on. This is what a UI-thread caller uses.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait, never the shared probe.</param>
    /// <returns>The context, or null when there is none.</returns>
    public static async Task<WslContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var resolution = Current;
        if (resolution.OverrideContext is { } context)
            return context;

        if (!OperatingSystem.IsWindows())
            return null;

        return await resolution.Probed.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the calling test its own resolution until the returned scope is disposed:
    /// its own <paramref name="override"/>, its own memoised probe run through
    /// <paramref name="prober"/>, and its own last probe. Only the async flow that opened
    /// the scope sees it, so a test running beside this one on another thread resolves
    /// exactly what it would have without it.
    /// </summary>
    /// <param name="override">The context the accessors return first, or null for none.</param>
    /// <param name="prober">
    /// What the memo runs. Defaults to a machine with no WSL, which is also what every
    /// platform other than Windows answers, so a test that states no prober behaves the
    /// same everywhere and never probes the real machine.
    /// </param>
    /// <returns>The scope; disposing it restores whatever the flow saw before.</returns>
    internal static IDisposable IsolateForTests(
        WslContext? @override = null, Func<CancellationToken, Task<WslProbe>>? prober = null)
    {
        var outer = Isolated.Value;
        var installed = new Resolution(prober ?? (_ => Task.FromResult(WslProbe.Empty)), @override);
        Isolated.Value = installed;
        return new IsolationScope(installed, outer);
    }

    /// <summary>
    /// The calling test's memoised probe, platform checks and override skipped. Off
    /// Windows the accessors never read the memo, so this is how a test observes it.
    /// Refused outside <see cref="IsolateForTests"/>: the process-wide memo is the real
    /// probe, and on Windows reading it would probe the machine running the suite.
    /// </summary>
    internal static Task<WslContext?> ProbedForTestsAsync() =>
        (Isolated.Value ?? throw new InvalidOperationException(
            "ProbedForTestsAsync reads a test's own memo; open WslContextResolver.IsolateForTests first."))
        .Probed.Value;

    /// <summary>
    /// Sets (or with null clears) the override that <see cref="TryGetCurrent"/> returns first,
    /// on the resolution in use: the process's, or inside <see cref="IsolateForTests"/> the
    /// calling test's own.
    /// </summary>
    public static void Override(WslContext? context) => Current.OverrideContext = context;

    /// <summary>
    /// The last probe this resolution ran, kept beside the context so a refusal can say
    /// WHICH check failed. Without it every Windows surface could only report "not
    /// installed or not on PATH", which names neither the distro nor the fix.
    /// </summary>
    private static WslProbe? LastProbe => Current.RecordedProbe;

    /// <summary>
    /// The probe that explains a host with NO resolved distro. That is the last one
    /// this resolver ran, unless it reports a usable distro: a usable probe cannot
    /// explain a missing context, so the empty one stands in and the gate still names
    /// a real first failing check rather than deciding there is nothing wrong.
    /// </summary>
    public static WslProbe UnusableProbe =>
        LastProbe is { IsUsable: false } probe ? probe : WslProbe.Empty;

    /// <summary>
    /// The context a usable probe resolves to; null for any unusable probe. Records
    /// nothing: only a resolution's own memo sets its <see cref="LastProbe"/>.
    /// </summary>
    public static WslContext? FromProbe(WslProbe probe) =>
        probe.IsUsable
            ? new WslContext(probe.WslExePath!, probe.DistroName!, probe.NonoPath!, probe.DistroHome!, probe.UserPath)
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
            WslVmPlatform.ThisMachine,
            ct);
    }

    internal static string? FindWslExe()
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
