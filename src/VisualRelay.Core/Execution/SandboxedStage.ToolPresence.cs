using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Pure, testable tool-presence probe: the required sandbox against PATH on the
// local host, against the resolved WSL distro on Windows. Kept separate so each
// file stays under the file-size guard.
public static partial class SandboxedStage
{
    /// <summary>What Windows needs instead of nono on PATH; named in the gate message.</summary>
    private const string WslRequirement = "a WSL2 distro with nono installed inside it";

    // Returns the required launch tools that do NOT resolve (empty ⇒ all present).
    // The sandbox is the only requirement and it is non-negotiable: every agent
    // command is wrapped in it. PATH and the binary name are injectable so a test
    // can simulate missing/present without touching the real PATH; the host is
    // injectable so the Windows arm (a resolved WSL context, not a binary on the
    // Windows PATH) is exercised anywhere. Callers probe the same PATH the launch uses.
    //
    // There used to be a second requirement, the CLI binary of the retired
    // subprocess agent. Nothing spawns it any more, and leaving it here would
    // report it missing on every machine forever.
    public static IReadOnlyList<string> MissingRequiredTools(
        RelayConfig config,
        string? pathValue = null,
        string nonoBinary = NonoBinary,
        SandboxHost? host = null) =>
        MissingRequiredTools(
            pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            nonoBinary, host ?? SandboxHost.Current,
            Environment.GetEnvironmentVariable("PATHEXT"));

    /// <summary>
    /// Pure tool-presence probe with the host injected. The requirement is nono on
    /// PATH on the local host and a resolved WSL2 distro with nono inside it on
    /// Windows. Returns the missing requirement names (empty ⇒ runnable).
    /// </summary>
    /// <param name="path">The PATH to resolve against.</param>
    /// <param name="nonoBinary">The sandbox binary name.</param>
    /// <param name="host">Where the sandbox is launched from.</param>
    /// <param name="pathext">PATHEXT, for Windows executable resolution.</param>
    /// <returns>The missing requirement names.</returns>
    private static IReadOnlyList<string> MissingRequiredTools(
        string path, string nonoBinary, SandboxHost host, string? pathext)
    {
        bool OnPath(string name) =>
            // A bare path component (no directory) is taken as a cwd-relative
            // executable, mirroring how the process launcher resolves it.
            Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar)
                ? File.Exists(name)
                : PathExecutables.Resolve(name, path, pathext, host.IsWindows, File.Exists) is not null;

        var missing = new List<string>(1);
        if (host.IsWindows)
        {
            if (host.Wsl is null)
                missing.Add(WslRequirement);
        }
        else if (!OnPath(nonoBinary))
        {
            missing.Add(nonoBinary);
        }

        return missing;
    }

    // Actionable, user-facing message for the fail-fast tool-presence gate. Names
    // the real cause (a missing binary on this host) so the user never sees the
    // sandbox advisory dump. internal (not private) so the GUI gate
    // (MainWindowViewModel.EnsureRunnableAsync) reuses the exact same copy instead
    // of hand-copying it — both surfaces stay identical.
    //
    // On a Windows host the generic line named neither the distro nor the fix, while
    // the probe behind the refusal knows exactly which check failed; the gate's own
    // message is rendered instead, so the run gate, bootstrap and create-config all
    // print the one fix table TROUBLESHOOTING.md documents. The probe is injectable
    // so the Windows arm is exercised on any OS.
    internal static string MissingToolsMessage(
        IReadOnlyList<string> missing, SandboxHost? host = null, WslProbe? probe = null)
    {
        if ((host ?? SandboxHost.Current).IsWindows
            && WslGate.Decide(probe ?? WslContextResolver.UnusableProbe).Message is { } gateMessage)
        {
            return gateMessage;
        }

        return $"{string.Join(" and ", missing)} is not installed or not on PATH on this machine — " +
            "Visual Relay can't run tasks here. It's set up on the VM, not this host. " +
            "Install it and retry.";
    }
}
