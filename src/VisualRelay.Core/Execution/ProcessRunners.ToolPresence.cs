using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Pure, testable tool-presence probe: checking required binaries (swival, nono)
// against PATH, with Windows-sandbox awareness. Kept separate from the diagnostics
// partial so each file stays under the file-size guard.
public sealed partial class SwivalSubagentRunner
{
    // Returns the required launch tools that do NOT resolve against PATH (empty
    // ⇒ all present). swival and nono are both always required: nono always wraps
    // swival (see BuildLaunchTarget), so the sandbox is non-negotiable. PATH and
    // the binary names are injectable so a test can simulate missing/present
    // without touching the real PATH; callers probe the same PATH the launch uses
    // (Environment.GetEnvironmentVariable).
    public static IReadOnlyList<string> MissingRequiredTools(
        RelayConfig config,
        string? pathValue = null,
        string swivalBinary = "swival",
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
            swivalBinary, nonoBinary, isWindows,
            Environment.GetEnvironmentVariable("PATHEXT"), windowsMode);
    }

    /// <summary>
    /// Pure tool-presence probe with the OS dispatch injected. swival is always
    /// required (resolved PATHEXT-aware on Windows so <c>swival.exe</c> is found);
    /// the second requirement is nono on Unix and a non-blocked Windows sandbox on
    /// Windows. Returns the missing requirement names (empty ⇒ runnable).
    /// </summary>
    internal static IReadOnlyList<string> MissingRequiredTools(
        string path, string swivalBinary, string nonoBinary,
        bool isWindows, string? pathext, WindowsSandboxMode windowsMode)
    {
        bool OnPath(string name) =>
            // A bare path component (no directory) is taken as a cwd-relative
            // executable, mirroring how the process launcher resolves it.
            Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar)
                ? File.Exists(name)
                : PathExecutables.Resolve(name, path, pathext, isWindows, File.Exists) is not null;

        var missing = new List<string>(2);
        if (!OnPath(swivalBinary))
            missing.Add(swivalBinary);

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
