using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>
/// The Windows arm of <see cref="SandboxPathInspector"/>. nono runs inside the WSL
/// distro, so the inspector asks the same <c>nono profile show</c> and
/// <c>nono profile groups</c> questions there, through a plain <c>wsl.exe --exec</c>
/// (no sandbox envelope). The profile it shows is the very copy a run enforces,
/// placed inside the distro by <see cref="NonoProfileEnsurer"/>; the platform is
/// Linux and <c>~</c> is the distro user's home. The denials it lists are the ones
/// Landlock enforces there, so no "may be readable" caveat exists any more.
/// </summary>
public static partial class SandboxPathInspector
{
    /// <summary>
    /// Inspects through <paramref name="context"/>: <paramref name="ensureProfile"/>
    /// places the profile inside the distro and yields its Linux path (the real one
    /// writes through the distro's share; a failure means there is nothing to show),
    /// and <paramref name="runWsl"/> runs each nono query (stdout on exit 0, else null).
    /// Both are injected so the arm is exercised without a distro.
    /// </summary>
    internal static async Task<SandboxInspectionResult> InspectThroughWslAsync(
        WslContext context,
        Func<WslLaunch, CancellationToken, Task<string?>> runWsl,
        Func<CancellationToken, Task<string>> ensureProfile,
        string? workspaceRoot, IReadOnlyList<string>? extraAllowPaths, CancellationToken cancellationToken)
    {
        string linuxProfile;
        try
        {
            linuxProfile = await ensureProfile(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return SandboxInspectionResult.Unavailable; }

        Task<string?> Nono(IReadOnlyList<string> args) =>
            runWsl(WslLauncher.BuildPlain(context.WslExePath, context.Distro, [context.NonoPath, .. args]), cancellationToken);

        return await ResolveAsync(
            () => Nono(["profile", "show", linuxProfile, "--json"]),
            name => Nono(["profile", "groups", name, "--json"]),
            SandboxPlatform.Linux, context.DistroHome, workspaceRoot, extraAllowPaths);
    }

    /// <summary>Runs one read-only nono query through wsl.exe; stdout on exit 0, else null.</summary>
    private static Task<string?> RunWslJsonAsync(WslLaunch launch, CancellationToken cancellationToken) =>
        RunJsonAsync(launch.FileName, launch.Arguments, launch.Environment, cancellationToken);
}
