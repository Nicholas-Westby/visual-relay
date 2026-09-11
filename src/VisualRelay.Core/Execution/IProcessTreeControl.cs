namespace VisualRelay.Core.Execution;

/// <summary>
/// How <see cref="ProcessCapture"/> watches and stops the process tree behind a
/// launched process when that tree is not visible to the host: the Linux tree
/// behind a wsl.exe relay, which Windows can neither sample (no CPU times) nor
/// kill (killing wsl.exe orphans the tree). Without a strategy the capture uses
/// the host's own signals and <c>ps</c>/Toolhelp, as before.
/// </summary>
public interface IProcessTreeControl
{
    /// <summary>
    /// Cumulative CPU time (ms) of the whole tree, or null when there is no
    /// signal yet (the tree has not registered, the sampler failed). Null is
    /// never activity; the capture drops its baseline and waits for the next sample.
    /// </summary>
    Task<long?> SampleCpuMsAsync(CancellationToken ct);

    /// <summary>
    /// <paramref name="graceful"/> asks the tree to stop (SIGTERM to the group);
    /// otherwise forces it (SIGKILL). The capture waits its grace window between
    /// the two and kills the local relay process last, so a failure here must be
    /// swallowed rather than thrown: the local kill still follows.
    /// </summary>
    Task StopAsync(bool graceful, CancellationToken ct);
}
