namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Recursively copies a SMALL git-ignored source directory into the verify
    /// worktree so the test command sees real, WRITABLE, ISOLATED content (a test
    /// that writes a git-ignored path stays inside the sandboxed cwd instead of
    /// following a symlink out to the source). Resilient by design:
    ///   • reparse points (symlinks) inside the tree are SKIPPED — never followed,
    ///     so a cycle or a link into the real tree can't be traversed or copied;
    ///   • a per-entry IO error is swallowed and the walk continues;
    ///   • parent dirs are created as needed.
    /// A copy failure must NEVER abort worktree creation (the caller also guards).
    /// </summary>
    private static void CopyDirectoryResilient(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        DirectoryInfo source;
        try
        {
            source = new DirectoryInfo(sourceDir);
            if (!source.Exists) return;
        }
        catch
        {
            return;
        }

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = source.EnumerateFileSystemInfos();
        }
        catch
        {
            return; // unauthorized / IO — leave the (empty) dest dir, never throw.
        }

        foreach (var entry in entries)
        {
            try
            {
                // Skip reparse points: following a symlink risks cycles and could copy
                // (or descend into) content outside this dir's own footprint.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var target = Path.Combine(destDir, entry.Name);
                if (entry is DirectoryInfo subDir)
                    CopyDirectoryResilient(subDir.FullName, target);
                else if (entry is FileInfo file)
                    file.CopyTo(target, overwrite: false);
            }
            catch
            {
                // Per-entry IO error (deleted mid-walk, denied, …) — skip it.
            }
        }
    }
}
