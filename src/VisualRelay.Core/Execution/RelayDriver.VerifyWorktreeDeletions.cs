namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Repo-relative tracked paths the agent DELETED relative to HEAD — covering BOTH a
    /// STAGED removal (<c>git rm</c>: gone from index AND working tree) and an UNSTAGED
    /// one (plain <c>rm</c>: gone from the working tree only). Uses
    /// <c>git diff HEAD --name-status --no-renames -z</c> and keeps status <c>D</c>:
    /// this is the ONLY form that reveals a STAGED deletion — <c>git ls-files --deleted</c>
    /// and an unstaged <c>git diff</c> both miss it because the index no longer lists the
    /// path. <c>--no-renames</c> makes a rename surface as delete(old)+add(new), so the
    /// old path is removed here and the new one is copied by the add/modify overlay.
    /// NUL-safe; keys purely on git status (no VR/toolchain specifics).
    /// </summary>
    private async Task<IReadOnlyList<string>> EnumerateDeletedTrackedAsync(string rootPath, CancellationToken cancellationToken)
    {
        var deleted = new List<string>();
        var diff = await _dependencies.GitInvoker.RunAsync(
            rootPath, new[] { "diff", "HEAD", "--name-status", "--no-renames", "-z" }, cancellationToken);
        // `-z --name-status` (renames off) is a flat <status>\0<path>\0… stream: every
        // record is exactly one status token followed by its path. Walk it in pairs.
        var tokens = SplitNul(diff.Output).ToList();
        for (var i = 0; i + 1 < tokens.Count; i += 2)
            if (tokens[i] == "D")
                deleted.Add(tokens[i + 1]);
        return deleted;
    }

    /// <summary>
    /// Removes a deleted tracked path from the verify worktree (the HEAD checkout
    /// resurrected it) and prunes any parent directories the removal left EMPTY, up to —
    /// but never including — <paramref name="worktreeRoot"/>. So a deleted file no longer
    /// reappears and a fully-deleted directory leaves no empty husk (git stores no empty
    /// dirs), matching the agent's working tree. A directory reparse point is UNLINKED
    /// (never recursed) for the same safety reason as the ignored-overlay cleanup.
    /// Best-effort per path — an IO error never aborts worktree creation.
    /// </summary>
    private static void RemoveDeletedOverlayPath(string worktreeRoot, string relative)
    {
        try
        {
            var dst = Path.Combine(worktreeRoot, relative);
            if (File.Exists(dst))
            {
                File.Delete(dst); // a file (or a file symlink) — removes the entry only
            }
            else if (Directory.Exists(dst))
            {
                // Defensive: a tracked path that is itself a directory. Unlink a reparse
                // point (never recurse into a link target); recurse a real directory.
                var isLink = File.GetAttributes(dst).HasFlag(FileAttributes.ReparsePoint);
                Directory.Delete(dst, recursive: !isLink);
            }

            // Prune now-empty parent dirs, never escaping or deleting the worktree root.
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
            var dir = Path.GetDirectoryName(dst);
            while (!string.IsNullOrEmpty(dir))
            {
                var dirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                if (dirFull.Length <= rootFull.Length ||
                    !dirFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    break; // reached / above the worktree root
                if (!Directory.Exists(dirFull) || Directory.EnumerateFileSystemEntries(dirFull).Any())
                    break; // already gone, or not empty → stop
                Directory.Delete(dirFull);
                dir = Path.GetDirectoryName(dirFull);
            }
        }
        catch
        {
            // Best-effort: never abort worktree creation on a delete/prune IO error.
        }
    }
}
