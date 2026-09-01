using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Pure, testable tool-presence probe: checking the required sandbox against PATH,
// with Windows-sandbox awareness. Kept separate so each file stays under the
// file-size guard.
public static partial class SandboxedStage
{
    // Returns the required launch tools that do NOT resolve against PATH (empty
    // ⇒ all present). The sandbox is the only requirement and it is
    // non-negotiable: every agent command is wrapped in it. PATH and the binary
    // name are injectable so a test can simulate missing/present without touching
    // the real PATH; callers probe the same PATH the launch uses.
    //
    // There used to be a second requirement, the `swival` binary. Nothing spawns
    // it any more, and leaving it here would report it missing on every machine
    // forever.
    public static IReadOnlyList<string> MissingRequiredTools(
        RelayConfig config,
        string? pathValue = null,
        string nonoBinary = NonoBinary)
    {
        var isWindows = OperatingSystem.IsWindows();
        // On Windows there is no nono; the required sandbox is MXC (or the degraded
        // builtin opt-in). Resolve its availability the same way the launch does.
        var windowsMode = isWindows
            ? WindowsSandbox.Select(
                Environment.GetEnvironmentVariable(WindowsSandbox.OptInEnvVar),
                MxcProvisioner.ResolveWxcExec() is not null)
            : WindowsSandboxMode.Mxc; // unused off Windows

        return MissingRequiredTools(
            pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            nonoBinary, isWindows,
            Environment.GetEnvironmentVariable("PATHEXT"), windowsMode);
    }

    /// <summary>
    /// Pure tool-presence probe with the OS dispatch injected. The requirement is
    /// nono on Unix and a non-blocked Windows sandbox on Windows. Returns the
    /// missing requirement names (empty ⇒ runnable).
    /// </summary>
    /// <param name="path">The PATH to resolve against.</param>
    /// <param name="nonoBinary">The sandbox binary name.</param>
    /// <param name="isWindows">Whether to take the Windows arm.</param>
    /// <param name="pathext">PATHEXT, for Windows executable resolution.</param>
    /// <param name="windowsMode">The selected Windows sandbox mode.</param>
    /// <returns>The missing requirement names.</returns>
    private static IReadOnlyList<string> MissingRequiredTools(
        string path, string nonoBinary,
        bool isWindows, string? pathext, WindowsSandboxMode windowsMode)
    {
        bool OnPath(string name) =>
            // A bare path component (no directory) is taken as a cwd-relative
            // executable, mirroring how the process launcher resolves it.
            Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar)
                ? File.Exists(name)
                : PathExecutables.Resolve(name, path, pathext, isWindows, File.Exists) is not null;

        var missing = new List<string>(1);
        if (isWindows)
        {
            if (windowsMode == WindowsSandboxMode.Blocked)
                missing.Add("a Windows sandbox (set VR_WINDOWS_SANDBOX=builtin or provision wxc-exec)");
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
    internal static string MissingToolsMessage(IReadOnlyList<string> missing) =>
        $"{string.Join(" and ", missing)} is not installed or not on PATH on this machine — " +
        "Visual Relay can't run tasks here. It's set up on the VM, not this host. " +
        "Install swival and retry.";
}
