namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// Everything the Windows gate needs to know about the machine's WSL, gathered by
/// <see cref="WslProber"/> and decided on by the CLI's WSL gate. A pure record so
/// the decision, the messages and the resolved launch context are all testable
/// without a Windows box. <see cref="Diagnostics"/> carries the raw facts behind
/// a failed check (the LSM list, the listing text, an error) for the message.
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
    /// <summary>Nothing found and nothing probed: what a machine without WSL looks like.</summary>
    public static WslProbe Empty { get; } =
        new(false, null, [], null, null, false, null, null, null, false, null, null);

    /// <summary>
    /// True only when every launch prerequisite holds: wsl.exe, a selected WSL2
    /// distro, nono resolvable inside it, Landlock active there, and the distro
    /// user's home (where the guard profile is placed).
    /// </summary>
    public bool IsUsable =>
        WslExeFound && DistroName is not null && IsWsl2 && NonoPath is not null && LandlockActive && DistroHome is not null;
}
