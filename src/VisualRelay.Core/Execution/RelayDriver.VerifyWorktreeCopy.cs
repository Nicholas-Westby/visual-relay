namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Recreates <paramref name="entry"/> as a symlink at the corresponding path
    /// under <paramref name="destDir"/>, applying three target-rewriting rules:
    ///   • relative targets → verbatim (resolves within the copied tree naturally)
    ///   • absolute targets inside sourceDir → prefix-swap sourceDir→destDir
    ///   • absolute targets outside sourceDir → verbatim (read-mostly sharing)
    /// Dangling targets are recreated verbatim (cp -RP semantics).
    /// Never follows or enumerates into the link.
    /// </summary>
    private static void RecreateSymlink(FileSystemInfo entry, string sourceDir, string destDir)
    {
        string? linkTarget;
        try { linkTarget = entry.LinkTarget; }
        catch { return; } // unreadable link target — skip, don't throw

        if (string.IsNullOrEmpty(linkTarget)) return;

        string resolvedTarget;
        if (Path.IsPathRooted(linkTarget))
        {
            // Absolute target: if it points inside sourceDir, rewrite to destDir.
            var normalizedTarget = Path.GetFullPath(linkTarget);
            var normalizedSource = Path.GetFullPath(sourceDir);
            if (normalizedTarget.StartsWith(normalizedSource + Path.DirectorySeparatorChar)
                || normalizedTarget == normalizedSource)
            {
                resolvedTarget = Path.GetFullPath(Path.Combine(
                    destDir, normalizedTarget[(normalizedSource.Length + 1)..]));
            }
            else
            {
                resolvedTarget = linkTarget; // outside sourceDir — verbatim
            }
        }
        else
        {
            resolvedTarget = linkTarget; // relative — verbatim
        }

        var targetPath = Path.Combine(destDir, entry.Name);
        if (entry is DirectoryInfo)
            Directory.CreateSymbolicLink(targetPath, resolvedTarget);
        else
            File.CreateSymbolicLink(targetPath, resolvedTarget);
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
