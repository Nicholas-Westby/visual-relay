namespace VisualRelay.Core.Execution;

/// <summary>
/// Removes the links an overlay put into a throwaway worktree, before the worktree goes. An
/// overlay links a checkout's large git-ignored entries (node_modules, a .venv) into verify
/// and planning worktrees, and a removal that followed such a link would delete the
/// checkout's real contents, so every removal unlinks them first.
/// </summary>
internal static class WorktreeLinks
{
    /// <summary>
    /// Recursively removes EVERY symlink (reparse point) inside <paramref name="worktreePath"/>,
    /// including symlinks nested inside directories created by the recursive overlay walk
    /// (which can contain directory symlinks at any depth).
    /// Real directories are recursed into; reparse points are unlinked as nodes
    /// (never traversed). Best-effort per entry — never throws.
    /// </summary>
    internal static void UnlinkAll(string worktreePath)
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
                    UnlinkAll(entry);
                }
            }
            catch
            {
                // Best-effort: leave it for git worktree remove / the dir delete fallback.
            }
        }
    }
}
