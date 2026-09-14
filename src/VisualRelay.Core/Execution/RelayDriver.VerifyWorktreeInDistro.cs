using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

// The verify snapshot's copies on the Windows arm, made by the distro instead of the app.
public sealed partial class RelayDriver
{
    /// <summary>How long one in-distro copy batch may run: a large ignored tree copies in one go.</summary>
    private static readonly TimeSpan InDistroCopyTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Copies into the snapshot inside the distro when the workspace and the snapshot are both
    /// shares of the sandbox's distro (<see cref="WslTreeCopy"/> says why the app's own copies
    /// break there). Returns <c>false</c>, having done nothing, when they are not, so the caller
    /// keeps its app-side copy. A name the distro could not place warns and moves on, as the
    /// app-side overlay does.
    /// </summary>
    private async Task<bool> TryCopyInDistroAsync(
        string sourcePath, string worktreePath, string worktreeId, string runId,
        Func<WslContext, string, string, WslLaunch?> build, CancellationToken cancellationToken)
    {
        var host = _dependencies.SandboxHost ?? await SandboxHost.CurrentAsync(cancellationToken);
        if (WslTreeCopy.InDistro(host, sourcePath, worktreePath) is not { } paths)
            return false;
        if (build(paths.Context, paths.Source, paths.Dest) is not { } launch)
            return true; // nothing to copy

        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), InDistroCopyTimeout, cancellationToken,
            environment: launch.Environment);
        var failed = WslTreeCopy.FailedNames(output);
        foreach (var name in failed)
            EmitOverlaySkipAdvisory(runId, sourcePath, worktreeId, name, "in_distro_copy_failed");
        if (timedOut || (exitCode != 0 && failed.Count == 0))
            EmitOverlaySkipAdvisory(runId, sourcePath, worktreeId, "*",
                timedOut ? "in_distro_copy_timed_out" : $"in_distro_copy_exit_{exitCode}");
        return true;
    }
}
