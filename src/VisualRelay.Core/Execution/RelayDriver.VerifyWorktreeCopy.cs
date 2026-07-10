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
    /// Git-ignored entries of <paramref name="sourcePath"/> as (relative path, isDirectory)
    /// pairs, suitable for overlaying the source's runtime content into a verify worktree.
    /// Uses <c>--directory</c> so a FULLY-ignored dir collapses to <c>name/</c> (trailing
    /// slash → directory) — entries are therefore DISJOINT: a nested entry (e.g.
    /// <c>packages/app/node_modules/</c>, the ignored part of a partially-tracked dir) is
    /// listed only when none of its ancestors is itself ignored, so its parents exist in
    /// the checkout. Nested entries are KEPT — dropping them broke per-package dependency
    /// resolution in workspace layouts (pnpm's <c>packages/*/node_modules</c>). VR/VCS
    /// internal names are excluded at ANY depth, and build-output dirs (see
    /// <see cref="BuildOutputOverlaySkipNames"/>) are skipped whether top-level or nested
    /// (an ignored FILE merely named like one is kept).
    /// </summary>
    private async Task<IReadOnlyList<(string Name, bool IsDirectory)>> EnumerateOverlayIgnoredEntriesAsync(
        string sourcePath, CancellationToken cancellationToken)
    {
        var result = new List<(string, bool)>();
        var ignored = await _dependencies.GitInvoker.RunAsync(
            sourcePath, new[] { "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z" }, cancellationToken);
        foreach (var raw in SplitNul(ignored.Output))
        {
            var isDirectory = raw.EndsWith('/');
            var name = isDirectory ? raw[..^1] : raw;
            if (name.Length == 0) continue;
            var segments = name.Split('/');
            if (segments.Any(IgnoredOverlayExcludedNames.Contains)) continue;
            // Build-output dirs are PATH-SENSITIVE (compilers bake the build path into
            // module caches / artifact DBs) and regenerable — OMIT them so the worktree
            // builds fresh at its own path instead of inheriting stale baked paths.
            // Applies to the dir itself and to anything beneath one.
            var dirSegments = isDirectory ? segments : segments[..^1];
            if (dirSegments.Any(BuildOutputOverlaySkipNames.Contains)) continue;
            result.Add((name, isDirectory));
        }
        return result;
    }
}
