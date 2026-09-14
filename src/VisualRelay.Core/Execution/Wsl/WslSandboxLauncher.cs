namespace VisualRelay.Core.Execution.Wsl;

/// <summary>A launch resolved for a workspace inside the distro: the wsl.exe process, the pid file its envelope writes, and the Linux workspace it changes into.</summary>
public sealed record WslSandboxLaunch(WslLaunch Launch, string PidFile, string LinuxWorkspace);

/// <summary>
/// The Windows launch site shared by the agent's command tools, the verify runner
/// and the bootstrap's validation shell. The workspace VR holds is the distro's
/// UNC share; here it becomes the Linux path the envelope changes into, the
/// workspace policy is applied, and the launch is built with a fresh pid file so
/// the tree can be sampled and stopped from inside the distro. Every refusal is a
/// message and nothing is launched: a workspace in another distro than the one
/// nono and the profile were set up in, a workspace outside any distro, or a
/// DrvFs workspace the policy refuses. Without a resolved distro at all the
/// callers refuse with <see cref="BlockedMessage"/> before coming here.
/// </summary>
public static class WslSandboxLauncher
{
    /// <summary>Why a Windows launch is refused when no usable distro was resolved.</summary>
    public const string BlockedMessage =
        "Windows task execution is blocked: no usable WSL2 distro with nono inside it was resolved, and "
        + "Visual Relay never runs a command unsandboxed. Run `visual-relay launch`: its gate names what is "
        + "missing (WSL, a WSL2 distro, nono inside it, Landlock) and how to install it. Visual Relay on "
        + "Windows runs your project's build and test commands inside that distro.";

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The Linux workspace <paramref name="rootPath"/> names inside the context's
    /// distro, or the refusal. A UNC root must be in that distro (the one nono and
    /// the profile were probed in); a Windows drive root is its DrvFs mount, which
    /// the policy decides on; anything else is not inside a distro at all.
    /// </summary>
    public static (string? LinuxWorkspace, string? Error) ResolveWorkspace(WslContext context, string rootPath)
    {
        string linuxWorkspace;
        if (WslPath.TryParseUnc(rootPath, out var distro, out var inDistro))
        {
            if (!distro.Equals(context.Distro, StringComparison.OrdinalIgnoreCase))
            {
                return (null,
                    $"The workspace '{rootPath}' lives in the WSL distro '{distro}', but Visual Relay resolved "
                    + $"'{context.Distro}' (nono and the guard profile are set up there). Set VR_WSL_DISTRO={distro} "
                    + $"and launch again, or move the repository into '{context.Distro}'.");
            }

            linuxWorkspace = inDistro;
        }
        else if (WslPath.TryDriveToMnt(rootPath, out var mount))
        {
            linuxWorkspace = mount;
        }
        else
        {
            return (null,
                $"The workspace '{rootPath}' is not inside a WSL distro. Visual Relay on Windows runs commands "
                + @"inside the distro, so open the repository through its share: \\wsl.localhost\<distro>\home\<user>\...");
        }

        // A warning (the downgrade of MntPolicy) has no channel at a launch site; the
        // launch proceeds and the explanation stays with the policy.
        var (decision, message) = WslWorkspacePolicy.Decide(linuxWorkspace);
        return decision == WslWorkspaceDecision.Refuse ? (null, message) : (linuxWorkspace, null);
    }

    /// <summary>
    /// Builds the launch of <paramref name="program"/> under <paramref name="nonoPrefix"/>
    /// (the absolute nono path and its arguments; empty for a plain shell) on the
    /// workspace <paramref name="rootPath"/> names, or the refusal. Every launch gets
    /// its own pid file, tagged so a stray file says which kind of launch left it.
    /// </summary>
    public static (WslSandboxLaunch? Launch, string? Error) Build(
        WslContext context, string rootPath, IReadOnlyList<string> nonoPrefix, string program,
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, string launchTag)
    {
        var (linuxWorkspace, error) = ResolveWorkspace(context, rootPath);
        if (linuxWorkspace is null)
            return (null, error);

        var pidFile = WslProcessControl.PidFilePath(Guid.NewGuid().ToString("N"), launchTag);
        var launch = WslLauncher.Build(
            context.WslExePath, context.Distro, linuxWorkspace, pidFile, nonoPrefix, program, args, WithUserPath(env, context));
        return (new WslSandboxLaunch(launch, pidFile, linuxWorkspace), null);
    }

    // A wsl.exe --exec child starts from the distro's default PATH, which lacks what the
    // user's profile adds (~/.cargo/bin, nvm's node); the probed login-shell PATH is what
    // the same command finds in their terminal, as the macOS environment snapshot is.
    internal static IReadOnlyDictionary<string, string>? WithUserPath(
        IReadOnlyDictionary<string, string>? env, WslContext context)
    {
        if (context.UserPath is null || env?.ContainsKey("PATH") == true)
            return env;
        var merged = env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(env, StringComparer.Ordinal);
        merged["PATH"] = context.UserPath;
        return merged;
    }

    /// <summary>The control that samples and stops the tree behind <paramref name="pidFile"/>, through real wsl.exe children.</summary>
    public static IProcessTreeControl TreeControl(WslContext context, string pidFile) =>
        new WslProcessTreeControl(context, pidFile, RunControlAsync);

    // The one place a control command (cat, ps, kill) runs: a plain wsl.exe child
    // whose whole output is captured; a timeout reads as a failure, never as a sample.
    private static async Task<(int ExitCode, string Output)> RunControlAsync(WslLaunch launch, CancellationToken ct)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), ControlTimeout, ct, environment: launch.Environment);
        return (timedOut ? -1 : exitCode, output);
    }
}
