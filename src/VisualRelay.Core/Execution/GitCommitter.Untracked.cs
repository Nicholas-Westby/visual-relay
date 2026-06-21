namespace VisualRelay.Core.Execution;

internal static partial class GitCommitter
{
    // Visual Relay's own run artifacts. These are never auto-committed (the
    // deliberate proof subset is force-added via proofFiles); everything else the
    // run authors is fair game. Auto-include must stay repo-agnostic — it must NOT
    // assume a src/tests/tools layout, since Visual Relay runs on any repo.
    private static readonly string[] InternalArtifactPrefixes = [".relay/", ".relay-scratch/", ".swival/"];

    /// <summary>
    /// Captures the set of untracked, non-ignored files at the start of a run.
    /// Uses <c>git ls-files --others --exclude-standard</c>, which respects
    /// <c>.gitignore</c>, <c>.git/info/exclude</c>, and the global gitignore.
    /// </summary>
    public static async Task<IReadOnlySet<string>> CaptureUntrackedSnapshotAsync(
        string rootPath,
        CancellationToken cancellationToken = default,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var result = await GitAsync(gi, rootPath, ["ls-files", "--others", "--exclude-standard"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed: {result.Output.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(result.Output))
            return new HashSet<string>(StringComparer.Ordinal);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
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

    /// <summary>
    /// Post-commit: returns any untracked, non-internal files absent from
    /// <paramref name="preRunUntracked"/> — files authored but not staged.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindUncommittedAuthoredFilesAsync(
        string rootPath,
        IReadOnlySet<string> preRunUntracked,
        string? tasksDir,
        CancellationToken cancellationToken = default,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var currentUntracked = await CaptureUntrackedSnapshotAsync(rootPath, cancellationToken, gi);
        var missed = new List<string>();
        foreach (var path in currentUntracked)
        {
            if (!preRunUntracked.Contains(path) && !IsInternalArtifact(path) && !IsUnderTasksDir(rootPath, path, tasksDir))
            {
                missed.Add(path);
            }
        }

        return missed;
    }
}
