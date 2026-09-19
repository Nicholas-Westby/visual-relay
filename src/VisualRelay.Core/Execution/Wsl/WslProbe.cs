namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// Everything the Windows gate needs to know about the machine's WSL, gathered by
/// <see cref="WslProber"/> and decided on by the CLI's WSL gate. A pure record so
/// the decision, the messages and the resolved launch context are all testable
/// without a Windows box. <see cref="Diagnostics"/> carries the raw facts behind
/// a failed check (nono's sandbox check, the listing text, an error) for the message.
/// </summary>
public sealed record WslProbe(
    bool WslExeFound,
    string? WslExePath,
    IReadOnlyList<WslDistro> Distros,
    string? RequestedDistro,
    string? DistroName,
    bool IsWsl2,
    string? KernelRelease,
    string? NonoPath,
    string? NonoVersion,
    bool LandlockActive,
    string? DistroHome,
    string? Diagnostics)
{
    /// <summary>
    /// True when wsl.exe exists but WSL itself does not: Windows 11 ships an inbox
    /// wsl.exe that answers every command with an install notice until WSL is installed.
    /// </summary>
    public bool WslPlatformMissing { get; init; }

    /// <summary>
    /// True when WSL is installed but the Virtual Machine Platform that WSL2 runs its Linux
    /// kernel on is not running (<see cref="WslVmPlatform.Running"/>): turned off, or turned on
    /// and waiting for a restart. No WSL2 distro can start then, and a distro install exits 0
    /// having installed nothing.
    /// </summary>
    public bool VmPlatformMissing { get; init; }

    /// <summary>
    /// True when Windows is waiting for a restart to finish installing or removing a component
    /// (<see cref="WslVmPlatform.RestartPending"/>). Read only with <see cref="VmPlatformMissing"/>,
    /// which that restart is what fixes once the platform has been turned on.
    /// </summary>
    public bool RestartPending { get; init; }

    /// <summary>
    /// The PATH the distro user's own login shell builds (profile and rc files, so
    /// rustup, nvm and the like count), or null when it could not be read. Every
    /// launch carries it; a plain <c>wsl --exec</c> gets only the distro default.
    /// </summary>
    public string? UserPath { get; init; }

    /// <summary>
    /// Where git resolves inside the distro against <see cref="UserPath"/>, or null
    /// when it does not resolve at all. Every workspace git call on Windows runs in
    /// the distro with that PATH, so a distro without git is not usable however well
    /// the sandbox works there.
    /// </summary>
    public string? GitPath { get; init; }

    /// <summary>Nothing found and nothing probed: what a machine without WSL looks like.</summary>
    public static WslProbe Empty { get; } =
        new(false, null, [], null, null, false, null, null, null, false, null, null);

    /// <summary>
    /// True when everything the SANDBOX launch needs holds: wsl.exe, a selected WSL2
    /// distro, nono resolvable inside it, Landlock active there, and the distro
    /// user's home (where the guard profile is placed). Separate from
    /// <see cref="IsUsable"/> because the login PATH and the git check are read only
    /// once these hold.
    /// </summary>
    public bool LaunchPrerequisitesMet =>
        WslExeFound && DistroName is not null && IsWsl2 && NonoPath is not null && LandlockActive && DistroHome is not null;

    /// <summary>
    /// True only when the distro can do everything Visual Relay asks of it: the
    /// launch prerequisites, plus git, because every workspace git call runs there.
    /// </summary>
    public bool IsUsable => LaunchPrerequisitesMet && GitPath is not null;
}
