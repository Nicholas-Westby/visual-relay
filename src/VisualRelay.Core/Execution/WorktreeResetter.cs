namespace VisualRelay.Core.Execution;

/// <summary>
/// Result of a worktree reset operation.
/// </summary>
/// <param name="Removed">Files that were actually deleted.</param>
/// <param name="Failed">Files the resetter intended to delete but could not
/// (e.g. the file did not exist on disk when the delete was attempted).</param>
/// <param name="SnapshotMissing">True when the pre-run untracked snapshot was
/// absent and the resetter refused to delete anything rather than act on an
/// unknown baseline.</param>
public sealed record WorktreeResetResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Failed,
    bool SnapshotMissing);

/// <summary>
/// Resets the worktree to HEAD after a flagged task so the next task in a drain
/// starts with a clean slate.  Safe to call with any repo — no-ops on non-git roots.
/// </summary>
internal static class WorktreeResetter
{
    private static readonly string[] InternalArtifactPrefixes =
        [".relay/", ".relay-scratch/", ".swival/"];

    /// <summary>
    /// Resets the worktree to HEAD after a flagged task, leaving the next task
    /// with a clean slate.  Safe to call with any repo: no-ops on non-git roots.
    /// </summary>
    internal static async Task<WorktreeResetResult> ResetAsync(
        string rootPath,
        string taskId,
        string? tasksDir,
        CancellationToken cancellationToken,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();

        // 1. Reset index + working tree to HEAD so no tracked changes survive.
        _ = await GitAsync(gi, rootPath, ["reset", "-q", "HEAD"], cancellationToken);
        _ = await GitAsync(gi, rootPath, ["checkout", "--", "."], cancellationToken);

        // 2. Remove untracked files authored by this task (not pre-existing ones).
        var snapshotPath = Path.Combine(rootPath, ".relay", taskId, "pre-run-untracked.txt");
        var preRunUntracked = File.Exists(snapshotPath)
            ? await ReadSnapshotAsync(snapshotPath, cancellationToken)
            : null;

        // Refuse to delete anything when the baseline snapshot is missing.
        // Acting on an unknown baseline is the dangerous default that can delete
        // every untracked file in the repo (see CapturePreRunUntrackedAsync in
        // RelayDriver.Snapshot.cs, which now always writes the snapshot when
        // CreateGitCommit is enabled).
        if (preRunUntracked is null)
        {
            return new WorktreeResetResult(
                Removed: Array.Empty<string>(),
                Failed: Array.Empty<string>(),
                SnapshotMissing: true);
        }

        var currentUntracked = await CaptureUntrackedAsync(gi, rootPath, cancellationToken);
        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var path in currentUntracked)
        {
            if (!preRunUntracked.Contains(path)
                && !IsInternalArtifact(path)
                && !IsUnderTasksDir(rootPath, path, tasksDir))
            {
                var full = Path.Combine(rootPath, path);
                if (File.Exists(full))
                {
                    File.Delete(full);
                    removed.Add(path);
                }
                else
                {
                    failed.Add(path);
                }
            }
        }

        // Remove any directories that are now empty as a result.
        foreach (var dir in removed
            .Select(r => Path.GetDirectoryName(Path.Combine(rootPath, r)))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d!.Length))
        {
            if (dir is not null && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }

        return new WorktreeResetResult(
            Removed: removed,
            Failed: failed,
            SnapshotMissing: false);
    }

    private static async Task<IReadOnlySet<string>> ReadSnapshotAsync(
        string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length > 0)
                set.Add(t);
        }
        return set;
    }

    private static async Task<IReadOnlySet<string>> CaptureUntrackedAsync(
        IGitInvoker gitInvoker, string rootPath, CancellationToken ct)
    {
        var result = await GitAsync(gitInvoker, rootPath, ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard"], ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return new HashSet<string>(StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in GitPathOutput.ParseLines(result.Output))
            set.Add(l);
        return set;
    }

    private static bool IsInternalArtifact(string relativePath)
    {
        foreach (var prefix in InternalArtifactPrefixes)
            if (relativePath.StartsWith(prefix, StringComparison.Ordinal)
                || string.Equals(relativePath, prefix.TrimEnd('/'), StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool IsUnderTasksDir(string rootPath, string relativePath, string? tasksDir)
    {
        if (string.IsNullOrEmpty(tasksDir))
            return false;
        // Deterministic relative-path prefix check — avoids NFC/NFD normalisation
        // mismatches on macOS where Path.GetFullPath may normalise while the
        // filesystem delivers NFD paths.
        if (relativePath == tasksDir
            || relativePath.StartsWith(tasksDir + "/", StringComparison.Ordinal)
            || relativePath.StartsWith(tasksDir + "\\", StringComparison.Ordinal))
            return true;
        // Fallback: resolve to full paths.
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var dirFullPath = Path.GetFullPath(Path.Combine(rootPath, tasksDir));
        return fullPath.StartsWith(dirFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, dirFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        IGitInvoker gitInvoker,
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken ct) =>
        gitInvoker.RunAsync(rootPath, arguments, ct);
}
