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
    /// path. <c>--no-renames</c> makes a rename (including a STAGED <c>git mv</c>) surface
    /// as delete(old)+add(new): the old path is removed here and the new one is copied by
    /// the add/modify overlay, which now enumerates <c>git diff HEAD</c> and so sees the
    /// staged add. NUL-safe; keys purely on git status (no VR/toolchain specifics).
    /// </summary>
    private async Task<IReadOnlyList<string>> EnumerateDeletedTrackedAsync(string rootPath, CancellationToken cancellationToken)
    {
        var diff = await _dependencies.GitInvoker.RunAsync(
            rootPath, new[] { "diff", "HEAD", "--name-status", "--no-renames", "-z" }, cancellationToken);
        return ParseDeletedTrackedPaths(diff.Output);
    }

    /// <summary>
    /// Extracts status-<c>D</c> (deleted) paths from a <c>git diff --name-status -z</c>
    /// stream. With <c>--no-renames</c> (which the caller keeps) every record is a flat
    /// <c>&lt;status&gt;\0&lt;path&gt;\0</c> pair. DEFENSIVE: if <c>--no-renames</c> is ever
    /// dropped, a rename/copy surfaces as a 3-token <c>R&lt;score&gt;\0src\0dst\0</c> record;
    /// this consumes BOTH of its paths so the walk stays ALIGNED (a naive pairwise walk
    /// would shift every later status onto a path and could remove the WRONG file or miss
    /// a real deletion). A rename src is deliberately NOT reported as a deletion (fail
    /// safe — under-remove, never over-remove; the dst is carried by the add/modify
    /// overlay). Only a single-char <c>D</c> is a deletion. Pure + NUL-safe; keys purely
    /// on git status (no VR/toolchain specifics).
    /// </summary>
    internal static IReadOnlyList<string> ParseDeletedTrackedPaths(string? nameStatusZ)
    {
        var deleted = new List<string>();
        var tokens = SplitNul(nameStatusZ).ToList();
        for (var i = 0; i < tokens.Count;)
        {
            var status = tokens[i];
            // Rename/copy records (R<score>/C<score>) carry TWO paths; all others carry ONE.
            var isRenameOrCopy = status.Length > 0 && (status[0] == 'R' || status[0] == 'C');
            var pathsInRecord = isRenameOrCopy ? 2 : 1;
            if (i + pathsInRecord >= tokens.Count)
                break; // truncated / unexpected tail — stop safely
            if (status == "D")
                deleted.Add(tokens[i + 1]);
            i += 1 + pathsInRecord;
        }
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
                File.Delete(dst); // a file (or a file/dangling symlink on Unix) — entry only
            }
            else if (Directory.Exists(dst))
            {
                // Defensive: a tracked path that is itself a directory. Unlink a reparse
                // point (never recurse into a link target); recurse a real directory.
                var isLink = File.GetAttributes(dst).HasFlag(FileAttributes.ReparsePoint);
                Directory.Delete(dst, recursive: !isLink);
            }
            else if (TryGetLinkAttributes(dst, out var attributes) &&
                     attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A DANGLING tracked symlink (target absent): on Windows File.Exists AND
                // Directory.Exists are BOTH false for a broken link, so neither branch
                // above fires and the resurrected link would survive. GetAttributes still
                // resolves the link's OWN attributes (it does not follow the missing
                // target), so unlink it here WITHOUT touching the target — a directory
                // reparse point non-recursively (recursive would delete the link target's
                // contents), a file/broken link via File.Delete.
                if (attributes.HasFlag(FileAttributes.Directory))
                    Directory.Delete(dst, recursive: false);
                else
                    File.Delete(dst);
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

    /// <summary>
    /// Reads an entry's OWN attributes WITHOUT following a symlink target: succeeds for a
    /// DANGLING link (GetAttributes does not follow the missing target) and returns
    /// <c>false</c> only when the entry is genuinely absent (GetAttributes throws). Lets
    /// the caller detect a broken reparse point that <see cref="File.Exists(string)"/> and
    /// <see cref="Directory.Exists(string)"/> both miss on Windows.
    /// </summary>
    private static bool TryGetLinkAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch
        {
            attributes = default;
            return false;
        }
    }
}
