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

    /// <summary>
    /// TOP-LEVEL git-ignored entries of <paramref name="sourcePath"/> as (name, isDirectory)
    /// pairs, suitable for overlaying the source's runtime content into a verify worktree.
    /// Uses <c>--directory</c> so a FULLY-ignored dir collapses to <c>name/</c> (trailing
    /// slash → directory); ignored files appear as plain paths. NESTED entries (those that
    /// still contain a <c>/</c> after the trailing slash is trimmed — e.g.
    /// <c>data/cache/</c>, the ignored part of a partially-tracked dir) are dropped: their
    /// parent is partially checked out and overlaying it whole would conflict. VR/VCS
    /// internal names and build-output dirs (see <see cref="BuildOutputOverlaySkipNames"/>)
    /// are excluded.
    /// </summary>
    private async Task<IReadOnlyList<(string Name, bool IsDirectory)>> EnumerateTopLevelIgnoredEntriesAsync(
        string sourcePath, CancellationToken cancellationToken)
    {
        var result = new List<(string, bool)>();
        var ignored = await _dependencies.GitInvoker.RunAsync(
            sourcePath, new[] { "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z" }, cancellationToken);
        foreach (var raw in SplitNul(ignored.Output))
        {
            var isDirectory = raw.EndsWith('/');
            var name = isDirectory ? raw[..^1] : raw;
            // Keep ONLY top-level entries (no path separator remains after trimming).
            if (name.Length == 0 || name.Contains('/')) continue;
            if (IgnoredOverlayExcludedNames.Contains(name)) continue;
            // Build-output dirs are PATH-SENSITIVE (compilers bake the build path into
            // module caches / artifact DBs) and regenerable — OMIT them so the worktree
            // builds fresh at its own path instead of inheriting stale baked paths.
            if (isDirectory && BuildOutputOverlaySkipNames.Contains(name)) continue;
            result.Add((name, isDirectory));
        }
        return result;
    }
}
