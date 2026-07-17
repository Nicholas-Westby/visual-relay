namespace VisualRelay.Core.Tasks;

public sealed partial class RelayTaskRepository
{
    /// <summary>
    /// Archives the live run-state directory for a flagged task by renaming
    /// .relay/<taskId>/ → .relay/<taskId>.reset-&lt;utc-stamp&gt;/. This removes the
    /// NEEDS-REVIEW marker, stage state, logs, and flagged-work bundle from the
    /// live path atomically while preserving everything on disk for post-mortem.
    /// If the run directory doesn't exist, it's a no-op.
    /// </summary>
    public void ResetTask(string taskId)
    {
        var runDir = Path.Combine(RootPath, ".relay", taskId);
        if (!Directory.Exists(runDir))
            return;

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss");
        var archiveDir = Path.Combine(RootPath, ".relay", $"{taskId}.reset-{stamp}");
        Directory.Move(runDir, archiveDir);
    }
}
