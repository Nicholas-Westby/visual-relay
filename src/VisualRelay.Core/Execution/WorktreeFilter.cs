namespace VisualRelay.Core.Execution;

/// <summary>
/// Result from <see cref="DiscardNonTestEditsAsync"/>.
/// </summary>
internal sealed record WorktreeFilterResult(
    IReadOnlyList<string> TrackedDiscarded,
    IReadOnlyList<string> UntrackedDeleted);

/// <summary>
/// Discards all worktree changes that are not in the declared test-files list,
/// so that after stage 5 (Author-tests) the worktree contains only the test
/// edits the agent authored.  Production-file modifications the agent made
/// during stage 5 are reverted to HEAD; untracked files outside testFiles are
/// deleted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compile stubs are unnecessary.</b> A test file may import a symbol that
/// does not yet exist (new interface, method, type). The test then fails to
/// compile, which counts as "red" — a compile failure is a legitimate red exit
/// code, not a green. No production stub is needed for the red-gate to pass.
/// If a test legitimately requires a new production stub to compile at all
/// (e.g. the missing type is in a different assembly), the compile failure
/// satisfies the red-check. Stage 6 (Implement) creates the real
/// implementation. The discard of any production edits the agent made during
/// stage 5 is therefore safe: compile-red is still red.
/// </para>
/// <para>
/// This filter is broader than <see cref="RedGate.ComputeStripSet"/>: it
/// catches production edits to files outside the manifest (which
/// <c>ComputeStripSet</c> never sees) and removes untracked files not listed
/// in testFiles.
/// </para>
/// </remarks>
internal static class WorktreeFilter
{
    // Mirror the internal-artifact prefixes used by GitCommitter and
    // WorktreeResetter so we never delete Visual Relay's own run data.
    private static readonly string[] InternalArtifactPrefixes =
        [".relay/", ".relay-scratch/", ".swival/"];

    /// <summary>
    /// Discard every dirty tracked and new untracked file that is <b>not</b>
    /// in <paramref name="testFiles"/>.  After this call the worktree contains
    /// only the test-file edits the agent declared.
    /// </summary>
    /// <param name="rootPath">Repository root.</param>
    /// <param name="testFiles">Paths the agent declared as test files (repo-relative).</param>
    /// <param name="tasksDir">Optional tasks-directory name (e.g. "llm-tasks") for the ledger note; used only to skip deletion of task files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="WorktreeFilterResult"/> describing what was discarded.</returns>
    internal static async Task<WorktreeFilterResult> DiscardNonTestEditsAsync(
        string rootPath,
        IReadOnlyList<string> testFiles,
        string? tasksDir,
        CancellationToken cancellationToken)
    {
        var testSet = new HashSet<string>(testFiles, StringComparer.Ordinal);

        // ── 1. Enumerate all dirty tracked files ────────────────────────
        // Combine unstaged (working-tree) and staged (index) diffs so we
        // catch every tracked change: modified, added, deleted, renamed, etc.
        // ── 1a. Tracked-file diffs (working-tree + staged) ─────────────
        var unstagedDiff = await GitAsync(rootPath, ["diff", "--name-only"], cancellationToken);
        var stagedDiff = await GitAsync(rootPath, ["diff", "--cached", "--name-only"], cancellationToken);
        var dirtyTracked = new List<string>();

        void AddLines(string output, List<string> target)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    target.Add(trimmed);
            }
        }

        AddLines(unstagedDiff.Output, dirtyTracked);
        AddLines(stagedDiff.Output, dirtyTracked);

        // Also include deleted tracked files (git diff --name-only does not
        // always list deleted files when the deletion is the only change).
        var deletedResult = await GitAsync(rootPath, ["ls-files", "--deleted"], cancellationToken);
        AddLines(deletedResult.Output, dirtyTracked);

        // ── 1b. Untracked files ────────────────────────────────────────
        var untrackedResult = await GitAsync(
            rootPath, ["ls-files", "--others", "--exclude-standard"], cancellationToken);
        var dirtyUntracked = new List<string>();
        AddLines(untrackedResult.Output, dirtyUntracked);

        // ── 2. Separate testFiles from non-test-files ──────────────────
        var nonTestTracked = dirtyTracked
            .Where(p => !testSet.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var nonTestUntracked = dirtyUntracked
            .Where(p => !testSet.Contains(p)
                && !IsInternalArtifact(p)
                && !IsUnderTasksDir(rootPath, p, tasksDir))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ── 3. Revert dirty tracked files to HEAD ──────────────────────
        // Use `git checkout HEAD -- <files>` to reset both the index and
        // working tree. Plain `git checkout -- <files>` only reverts the
        // working tree to the index, so staged changes would survive.
        if (nonTestTracked.Count > 0)
        {
            await GitAsync(rootPath, ["checkout", "HEAD", "--", .. nonTestTracked], cancellationToken);
        }

        // ── 4. Delete untracked non-test files ──────────────────────────
        foreach (var rel in nonTestUntracked)
        {
            var full = Path.Combine(rootPath, rel);
            if (File.Exists(full))
                File.Delete(full);
        }

        // Remove empty leaf directories left after deletions.
        foreach (var dir in nonTestUntracked
            .Select(r => Path.GetDirectoryName(Path.Combine(rootPath, r)))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d!.Length))
        {
            if (dir is not null && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }

        return new WorktreeFilterResult(nonTestTracked, nonTestUntracked);
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
        CancellationToken cancellationToken) =>
        GitInvoker.RunAsync(rootPath, arguments, cancellationToken);
}
