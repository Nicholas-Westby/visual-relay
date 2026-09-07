namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Best-effort teardown: git worktree remove, then a resilient dir delete. Takes
    /// no cancellation token on purpose — every caller reaches it from a
    /// <c>finally</c> that a cancel may already have tripped, and a cancelled token
    /// would skip the `git worktree remove` and leave the worktree registered.
    /// </summary>
    private async Task CleanupVerifyWorktreeAsync(string sourcePath, string worktreePath)
    {
        // SAFETY-CRITICAL: unlink the symlinks we added FIRST. A `git worktree remove`
        // or a recursive Directory.Delete that traversed a DIRECTORY symlink would
        // delete the REAL node_modules/.env contents in the source repo. Remove the
        // LINKS only (never recursive on a reparse point) so nothing can follow them.
        UnlinkOverlaySymlinks(worktreePath);
        await PlanningWorktree.RemoveAsync(sourcePath, worktreePath, _dependencies.GitInvoker, CancellationToken.None,
            timeProvider: _dependencies.TimeProvider);
        try { if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true); }
        catch { /* PRODUCTION fallback — never reference TestFileSystem here (Defect E). */ }
    }

    /// <summary>
    /// Recursively removes EVERY symlink (reparse point) inside <paramref name="worktreePath"/>,
    /// including symlinks nested inside directories created by the recursive overlay walk
    /// (which can contain directory symlinks at any depth).
    /// Real directories are recursed into; reparse points are unlinked as nodes
    /// (never traversed). Best-effort per entry — never throws.
    /// </summary>
    private static void UnlinkOverlaySymlinks(string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(worktreePath))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Unlink the symlink node — NEVER recursive-delete through it.
                    if (attributes.HasFlag(FileAttributes.Directory))
                        Directory.Delete(entry, recursive: false);
                    else
                        File.Delete(entry);
                }
                else if (attributes.HasFlag(FileAttributes.Directory))
                {
                    // Real directory — recurse to unlink any nested symlinks inside.
                    UnlinkOverlaySymlinks(entry);
                }
            }
            catch
            {
                // Best-effort: leave it for git worktree remove / the dir delete fallback.
            }
        }
    }
}
