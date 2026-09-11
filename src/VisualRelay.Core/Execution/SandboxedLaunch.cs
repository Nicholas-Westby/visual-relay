using System.Collections.Frozen;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>
/// One resolved launch: the process to start (nono on the local host, or wsl.exe
/// running the envelope inside the distro), the environment that process gets and
/// the names stripped from it, and, behind wsl.exe, the control for the Linux tree
/// the host can neither sample nor signal (null when the host sees the tree).
/// </summary>
internal sealed record SandboxedLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlySet<string> EnvironmentRemove,
    IProcessTreeControl? TreeControl)
{
    /// <summary>
    /// The launch behind wsl.exe: the process gets only the two WSL variables
    /// (the target's environment already travels inside the argv), nothing is
    /// stripped from it, and the tree is controlled through the pid file the
    /// envelope writes.
    /// </summary>
    internal static SandboxedLaunch ForWsl(WslContext context, WslSandboxLaunch launch) =>
        new(launch.Launch.FileName, launch.Launch.Arguments, launch.Launch.Environment, FrozenSet<string>.Empty,
            WslSandboxLauncher.TreeControl(context, launch.PidFile));
}
