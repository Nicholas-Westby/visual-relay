namespace VisualRelay.Core.Execution;

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
    internal static async Task ResetAsync(
        string rootPath,
        string taskId,
        string? tasksDir,
        CancellationToken cancellationToken)
    {
        // 1. Reset index + working tree to HEAD so no tracked changes survive.
        _ = await GitAsync(rootPath, ["reset", "-q", "HEAD"], cancellationToken);
        _ = await GitAsync(rootPath, ["checkout", "--", "."], cancellationToken);

        // 2. Remove untracked files authored by this task (not pre-existing ones).
        var snapshotPath = Path.Combine(rootPath, ".relay", taskId, "pre-run-untracked.txt");
        var preRunUntracked = File.Exists(snapshotPath)
            ? await ReadSnapshotAsync(snapshotPath, cancellationToken)
            : new HashSet<string>(StringComparer.Ordinal);

        var currentUntracked = await CaptureUntrackedAsync(rootPath, cancellationToken);
        var toDelete = new List<string>();
        foreach (var path in currentUntracked)
        {
            if (!preRunUntracked.Contains(path)
                && !IsInternalArtifact(path)
                && !IsUnderTasksDir(rootPath, path, tasksDir))
            {
                toDelete.Add(path);
            }
        }

        foreach (var rel in toDelete)
        {
            var full = Path.Combine(rootPath, rel);
            if (File.Exists(full))
                File.Delete(full);
        }

        // Remove any directories that are now empty as a result.
        foreach (var dir in toDelete
            .Select(r => Path.GetDirectoryName(Path.Combine(rootPath, r)))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d!.Length))
        {
            if (dir is not null && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
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
        string rootPath, CancellationToken ct)
    {
        var result = await GitAsync(rootPath, ["ls-files", "--others", "--exclude-standard"], ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return new HashSet<string>(StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in result.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = l.Trim();
            if (t.Length > 0)
                set.Add(t);
        }
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
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var dirFullPath = Path.GetFullPath(Path.Combine(rootPath, tasksDir));
        return fullPath.StartsWith(dirFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, dirFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken ct) =>
        GitInvoker.RunAsync(rootPath, arguments, ct);
}
