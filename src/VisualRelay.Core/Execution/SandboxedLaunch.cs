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
    IProcessTreeControl? TreeControl,
    string? WorkingDirectory = null)
{
    /// <summary>
    /// Where the launched process starts. Normally the workspace; behind wsl.exe the
    /// launch pins its own, because wsl.exe's Windows working directory only has to
    /// be one it can translate and a UNC workspace is not reliably that. Nothing
    /// needs it: the envelope does its own <c>cd</c> inside the distro.
    /// </summary>
    /// <param name="rootPath">The workspace, used when the launch pins nothing.</param>
    /// <returns>The directory to start the process in.</returns>
    internal string StartIn(string rootPath) => WorkingDirectory ?? rootPath;

    /// <summary>
    /// The launch behind wsl.exe: the process gets only the two WSL variables
    /// (the target's environment already travels inside the argv), nothing is
    /// stripped from it, the tree is controlled through the pid file the envelope
    /// writes, and wsl.exe itself is started from the Windows temp directory —
    /// the one directory it can always translate.
    /// </summary>
    internal static SandboxedLaunch ForWsl(WslContext context, WslSandboxLaunch launch) =>
        new(launch.Launch.FileName, launch.Launch.Arguments, launch.Launch.Environment, FrozenSet<string>.Empty,
            WslSandboxLauncher.TreeControl(context, launch.PidFile), Path.GetTempPath());
}
