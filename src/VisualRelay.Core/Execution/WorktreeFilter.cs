namespace VisualRelay.Core.Execution;

/// <summary>
/// Result from <see cref="DiscardNonTestEditsAsync"/>.
/// </summary>
internal sealed record WorktreeFilterResult(
    IReadOnlyList<string> TrackedDiscarded,
    IReadOnlyList<string> UntrackedDeleted,
    string? Error = null);

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
    /// Normalize an agent-supplied repo-relative path (or a git-emitted
    /// path) into a canonical form so that the set-based <c>Contains</c>
    /// checks are robust against formatting variance.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Strips a single leading <c>+</c> (stage-4 manifest new-file convention).</item>
    /// <item>Replaces <c>\</c> with <c>/</c> (agents may emit Windows separators).</item>
    /// <item>Trims a leading <c>./</c> and any leading <c>/</c>.</item>
    /// <item>Trims a trailing <c>/</c>.</item>
    /// </list>
    /// </remarks>
    private static string NormalizeRepoRelativePath(string path)
    {
        if (path.Length > 0 && path[0] == '+')
            path = path[1..];
        path = path.Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        path = path.TrimStart('/');
        path = path.TrimEnd('/');
        return path;
    }

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
        var testSet = new HashSet<string>(
            testFiles.Select(NormalizeRepoRelativePath),
            StringComparer.OrdinalIgnoreCase);

        // ── 0. Pre-check: are we inside a git worktree? ────────────────
        // If not, there is nothing to discard — return empty immediately.
        // This also avoids flagging tests that don't set up a git repo.
        var insideResult = await GitAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (insideResult.ExitCode != 0 || insideResult.TimedOut
            || !string.Equals(insideResult.Output.Trim(), "true", StringComparison.Ordinal))
        {
            return new WorktreeFilterResult([], []);
        }

        // ── 1. Enumerate all dirty tracked files ────────────────────────
        // Combine unstaged (working-tree) and staged (index) diffs so we
        // catch every tracked change: modified, added, deleted, renamed, etc.
        // ── 1a. Tracked-file diffs (working-tree + staged) ─────────────
        var unstagedDiff = await GitAsync(rootPath, ["diff", "--name-only"], cancellationToken);
        if (unstagedDiff.ExitCode != 0 || unstagedDiff.TimedOut)
            return new WorktreeFilterResult([], [], "git diff --name-only failed");
        // Use --name-status -M for the staged diff so we detect both old
        // and new names of staged renames (e.g. after `git mv`).
        var stagedDiff = await GitAsync(rootPath, ["diff", "--cached", "--name-status", "-M"], cancellationToken);
        if (stagedDiff.ExitCode != 0 || stagedDiff.TimedOut)
            return new WorktreeFilterResult([], [], "git diff --cached --name-status failed");
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

        void AddNameStatusLines(string output, List<string> target)
        {
            // Parses `git diff --cached --name-status -M` output.
            // Format: <status>\t<path>  or  <status>\t<old>\t<new> (rename).
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;
                // Status letter is the first character; paths follow after tabs.
                var parts = trimmed.Split('\t');
                if (parts.Length >= 2)
                {
                    if (parts[0].Length > 0 && parts[0][0] == 'R' && parts.Length >= 3)
                    {
                        // Rename: capture both old and new names.
                        var oldName = parts[1].Trim();
                        var newName = parts[2].Trim();
                        if (oldName.Length > 0)
                            target.Add(oldName);
                        if (newName.Length > 0)
                            target.Add(newName);
                    }
                    else
                    {
                        var path = parts[1].Trim();
                        if (path.Length > 0)
                            target.Add(path);
                    }
                }
            }
        }

        AddLines(unstagedDiff.Output, dirtyTracked);
        AddNameStatusLines(stagedDiff.Output, dirtyTracked);

        // Also include deleted tracked files (git diff --name-only does not
        // always list deleted files when the deletion is the only change).
        var deletedResult = await GitAsync(rootPath, ["ls-files", "--deleted"], cancellationToken);
        if (deletedResult.ExitCode != 0 || deletedResult.TimedOut)
            return new WorktreeFilterResult([], [], "git ls-files --deleted failed");
        AddLines(deletedResult.Output, dirtyTracked);

        // ── 1b. Untracked files ────────────────────────────────────────
        var untrackedResult = await GitAsync(
            rootPath, ["ls-files", "--others", "--exclude-standard"], cancellationToken);
        if (untrackedResult.ExitCode != 0 || untrackedResult.TimedOut)
            return new WorktreeFilterResult([], [], "git ls-files --others failed");
        var dirtyUntracked = new List<string>();
        AddLines(untrackedResult.Output, dirtyUntracked);

        // ── 2. Separate testFiles from non-test-files ──────────────────
        var nonTestTracked = dirtyTracked
            .Where(p => !testSet.Contains(NormalizeRepoRelativePath(p))
                && !IsInternalArtifact(p)
                && !IsUnderTasksDir(rootPath, p, tasksDir))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var nonTestUntracked = dirtyUntracked
            .Where(p => !testSet.Contains(NormalizeRepoRelativePath(p))
                && !IsInternalArtifact(p)
                && !IsUnderTasksDir(rootPath, p, tasksDir))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ── 3. Revert dirty tracked files to HEAD ──────────────────────
        // Revert each path individually so a staged rename (new path absent
        // from HEAD) cannot abort the batch.  When checkout fails the path
        // is unstaged via `git rm --cached` and deleted from disk.
        if (nonTestTracked.Count > 0)
        {
            var revertErrors = new List<string>();
            foreach (var rel in nonTestTracked)
            {
                var checkoutResult = await GitAsync(rootPath, ["checkout", "HEAD", "--", rel], cancellationToken);
                if (checkoutResult.ExitCode != 0 || checkoutResult.TimedOut)
                {
                    // Path absent from HEAD (staged rename destination,
                    // new staged file).  Unstage and delete.
                    var rmResult = await GitAsync(rootPath, ["rm", "--cached", "--", rel], cancellationToken);
                    if (rmResult.ExitCode != 0 || rmResult.TimedOut)
                        revertErrors.Add($"{rel}: checkout={checkoutResult.ExitCode}, rm={rmResult.ExitCode}");
                    var fullPath = Path.Combine(rootPath, rel);
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
            }
            if (revertErrors.Count > 0)
                return new WorktreeFilterResult(nonTestTracked, nonTestUntracked,
                    $"revert failures: {string.Join("; ", revertErrors)}");
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
