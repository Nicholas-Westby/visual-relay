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
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        var gi = gitInvoker;
        var tp = timeProvider ?? TimeProvider.System;
        var result = await GitAsync(gi, rootPath, ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard"], cancellationToken, timeProvider: tp);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed: {result.Output.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(result.Output))
            return new HashSet<string>(StringComparer.Ordinal);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in GitPathOutput.ParseLines(result.Output))
            set.Add(line);

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

    /// <summary>
    /// Post-commit: returns any untracked, non-internal files absent from
    /// <paramref name="preRunUntracked"/> — files authored but not staged.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindUncommittedAuthoredFilesAsync(
        string rootPath,
        IReadOnlySet<string> preRunUntracked,
        string? tasksDir,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        var gi = gitInvoker;
        var currentUntracked = await CaptureUntrackedSnapshotAsync(rootPath, gi, cancellationToken, timeProvider);
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
