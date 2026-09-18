using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Canned <see cref="WslProbe"/> records shared by the gate, context and CLI
/// decision tests: one fully usable machine, and the same machine with exactly
/// one check failing, so each test names the check it is about.
/// </summary>
internal static class WslProbeFixtures
{
    public const string WslExe = @"C:\Windows\System32\wsl.exe";
    private const string Kernel = "5.15.167.4-microsoft-standard-WSL2";

    private static readonly WslDistro Ubuntu = new("Ubuntu", 2, true, "Running");
    private static readonly WslDistro Debian = new("Debian", 1, false, "Stopped");

    public static WslProbe Usable(string distroName = "Ubuntu") => new(
        WslExeFound: true, WslExePath: WslExe, Distros: [Ubuntu, Debian], RequestedDistro: null,
        DistroName: distroName, IsWsl2: true, KernelRelease: Kernel, NonoPath: "/usr/local/bin/nono",
        NonoVersion: "nono 0.75.0", LandlockActive: true, DistroHome: "/home/alice", Diagnostics: null)
    { GitPath = "/usr/bin/git" };

    public static WslProbe NoWsl() => WslProbe.Empty;

    public static WslProbe InboxStubOnly() => NoDistro() with { WslPlatformMissing = true };

    public static WslProbe NoDistro() => Usable() with { Distros = [], DistroName = null, IsWsl2 = false, KernelRelease = null, NonoPath = null, NonoVersion = null, LandlockActive = false, DistroHome = null, GitPath = null };

    public static WslProbe RequestedDistroMissing() => NoDistro() with { Distros = [Ubuntu, Debian], RequestedDistro = "Fedora" };

    public static WslProbe Wsl1() => Usable() with { DistroName = "Debian", IsWsl2 = false, KernelRelease = "4.4.0-19041-Microsoft" };

    public static WslProbe NonoMissing() => Usable() with { NonoPath = null, NonoVersion = null };

    public static WslProbe LandlockInactive() => Usable() with { LandlockActive = false, Diagnostics = "nono setup --check-only (exit 1): nono: Setup error: Landlock is not available: No supported Landlock ABI detected" };

    public static WslProbe HomeUnknown() => Usable() with { DistroHome = null };

    public static WslProbe GitMissing() => Usable() with { GitPath = null };
}
