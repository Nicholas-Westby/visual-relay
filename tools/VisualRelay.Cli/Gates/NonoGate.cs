using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// The OS-level sandbox prerequisite (ported from the launcher's
/// <c>_require_nono</c>). nono is a hard, always-required dependency: on macOS
/// and Linux it must be on PATH; on Windows it must be installed inside a usable
/// WSL2 distro, which is probed once here and decided by <see cref="WslGate"/>.
/// When the prerequisite is missing, prints the fix and signals a hard failure
/// (exit 127). When present there is nothing to provision: the vr-guard profile
/// is owned and self-healed by the app at run start, and it inherits nono's
/// built-in <c>default</c>, so no pack has to be pulled first.
/// </summary>
public static class NonoGate
{
    /// <summary>
    /// Ensures the sandbox is available. Returns 0 to proceed, or 127 when the
    /// prerequisite is missing (after printing the fix). On Windows a successful
    /// probe also becomes the process-wide <see cref="WslContext"/>, so anything
    /// else running in this process reuses the same facts instead of probing again.
    /// A Windows failure that <c>setup-wsl</c> can finish is first offered as a y/N
    /// question (see <see cref="WslSetupOffer"/>); a yes that works lets the launch go on.
    /// </summary>
    public static int Require(string root)
    {
        var isWindows = OperatingSystem.IsWindows();
        var probe = isWindows ? WslContextResolver.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult() : null;
        var (exitCode, message) = Decide(ProcessLauncher.OnPath("nono"), isWindows, probe);
        if (exitCode != 0 && probe is not null)
        {
            var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
            var setup = WslSetupOffer.OfferAsync(probe, WslSetupHost.ThisMachine(), interactive, Console.In, Console.Error,
                CancellationToken.None).GetAwaiter().GetResult();
            if (setup is { Ready: null } failed)
                return failed.ExitCode; // the setup has already said why
            if (setup is { Ready: { } ready })
                (probe, exitCode, message) = (ready, 0, null);
        }

        if (message is not null)
            Console.Error.WriteLine(message);
        if (exitCode == 0 && probe is not null)
            WslContextResolver.Override(WslContextResolver.FromProbe(probe));
        return exitCode;
    }

    /// <summary>
    /// Pure OS-aware gate decision. On Windows the decision is the WSL gate's,
    /// made from <paramref name="probe"/> (an un-probed machine counts as one
    /// without WSL); nono on the Windows PATH is irrelevant because the sandbox
    /// binary lives inside the distro. Elsewhere nono on PATH proceeds (0) and a
    /// missing nono hard-fails (127) with install instructions. The returned
    /// message (when non-null) is what <see cref="Require"/> prints to stderr.
    /// </summary>
    public static (int ExitCode, string? Message) Decide(bool onPath, bool isWindows, WslProbe? probe)
    {
        if (isWindows)
            return WslGate.Decide(probe ?? WslProbe.Empty);

        if (onPath)
            return (0, null);

        return (127,
            """
            visual-relay: nono was not found on PATH.

              nono is a required dependency for the OS-level sandbox (Seatbelt on macOS,
              Landlock on Linux) that confines the agent's writes and deletes to the workspace.
              The sandbox is always on; there is no opt-out. Install nono:

                brew install nono
                (or see https://github.com/nolabs-ai/nono for other platforms)

              If Nix is installed, the devshell provides nono automatically.
            """);
    }
}
