using System.Globalization;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The argv VR runs inside the distro (through <see cref="WslLauncher.BuildPlain"/>)
/// to control the sandboxed tree the <see cref="WslLauncher.Envelope"/> started:
/// the envelope's setsid made the sandbox root the leader of its own process
/// group, so one signal to <c>-pgid</c> reaches the whole tree; the pid file it
/// wrote is how that pgid is learned; and the processes' /proc stat lines are the CPU sample
/// the shared tree summation reads.
/// </summary>
public static class WslProcessControl
{
    /// <summary>Inside the distro; the envelope creates it before writing the pid file.</summary>
    public const string PidFileDirectory = "/tmp/visual-relay";

    /// <summary><c>kill -&lt;signal&gt; -- -&lt;pgid&gt;</c>: the whole process group, never group 0 or every process.</summary>
    public static IReadOnlyList<string> KillArgv(int pgid, string signal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pgid);
        return ["kill", "-" + signal, "--", "-" + pgid.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Every process's /proc stat line, the CPU sample the tree summation reads. Not <c>ps</c>:
    /// procps prints TIME in whole seconds, too coarse to tell a quiet tree from an idle one.
    /// </summary>
    public static IReadOnlyList<string> SampleArgv() => ["/bin/sh", "-c", ProcessTreeCpuSampler.ProcStatScript];

    public static IReadOnlyList<string> ReadPidFileArgv(string pidFile) => ["cat", pidFile];

    /// <summary>
    /// <c>rm -f -- &lt;pidFile&gt;</c>: the envelope writes the file and exits, so the
    /// reap step is the only thing that can take it away again.
    /// </summary>
    /// <param name="pidFile">The pid file the envelope wrote.</param>
    /// <returns>The removal argv.</returns>
    public static IReadOnlyList<string> RemovePidFileArgv(string pidFile) => ["rm", "-f", "--", pidFile];

    public static string PidFilePath(string runId, string attemptTag) =>
        $"{PidFileDirectory}/{runId}-{attemptTag}.pid";
}
