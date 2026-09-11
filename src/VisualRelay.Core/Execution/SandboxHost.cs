using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Where a sandboxed command is launched from, as far as the launch shape cares.
/// On macOS and Linux nono runs on this machine and sees the same paths VR does
/// (<see cref="Local"/>). On Windows nono runs inside the resolved WSL2 distro
/// (<see cref="Windows"/> with a <see cref="WslContext"/>): the launch is a wsl.exe
/// invocation, the profile is loaded from its Linux placement and a directory VR
/// grants is named as the distro sees it; with no resolved distro every launch is
/// blocked, never run unsandboxed. Injected into the launch sites, so the Windows
/// arm is exercised on any OS without touching the process-wide resolver.
/// </summary>
public sealed record SandboxHost(bool IsWindows, WslContext? Wsl)
{
    /// <summary>nono runs on this machine.</summary>
    public static SandboxHost Local { get; } = new(false, null);

    /// <summary>nono runs inside the distro of <paramref name="wsl"/>; null when no usable distro was resolved.</summary>
    public static SandboxHost Windows(WslContext? wsl) => new(true, wsl);

    /// <summary>This machine: the local host off Windows; on Windows the memoized real probe.</summary>
    public static SandboxHost Current =>
        OperatingSystem.IsWindows() ? Windows(WslContextResolver.TryGetCurrent()) : Local;

    /// <summary>
    /// This machine, resolved without blocking. The first Windows resolution is a
    /// six-step wsl.exe probe, so a caller that can await — anything reachable from
    /// the UI thread — takes this one and leaves the memo warm for every synchronous
    /// reader after it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait, never the shared probe.</param>
    /// <returns>The host this machine launches from.</returns>
    public static Task<SandboxHost> CurrentAsync(CancellationToken cancellationToken = default) =>
        ResolveAsync(OperatingSystem.IsWindows(), cancellationToken);

    /// <summary>
    /// The host of a machine whose platform is given, so the Windows arm is resolved
    /// on any OS (with an overridden context) without a platform check per caller.
    /// </summary>
    /// <param name="isWindows">Whether the sandbox runs inside a WSL distro.</param>
    /// <param name="cancellationToken">Cancels the wait, never the shared probe.</param>
    /// <returns>The resolved host.</returns>
    internal static async Task<SandboxHost> ResolveAsync(bool isWindows, CancellationToken cancellationToken = default) =>
        isWindows
            ? Windows(await WslContextResolver.TryGetCurrentAsync(cancellationToken).ConfigureAwait(false))
            : Local;

    /// <summary>The absolute path nono loads the guard profile from, as nono sees it.</summary>
    public string ProfilePath =>
        Wsl is { } context
            ? WslProfilePlacement.For(context.Distro, context.DistroHome).LinuxPath
            : NonoProfileEnsurer.ResolveProfilePath();

    /// <summary>
    /// A directory VR grants (<c>-a</c>), as nono sees it: itself on the local host;
    /// inside the distro, the DrvFs mount of a Windows drive path, the Linux path of
    /// a UNC path into that same distro, or an absolute Linux path as it is. Null
    /// when the distro cannot reach the path (another distro, a relative path).
    /// </summary>
    public string? MapGrant(string path)
    {
        if (!IsWindows)
            return path;
        if (WslPath.TryParseUnc(path, out var distro, out var linuxPath))
            return Wsl is not null && distro.Equals(Wsl.Distro, StringComparison.OrdinalIgnoreCase) ? linuxPath : null;
        if (WslPath.TryDriveToMnt(path, out var mount))
            return mount;
        return path.StartsWith('/') ? path : null;
    }
}
