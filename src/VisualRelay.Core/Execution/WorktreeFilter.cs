namespace VisualRelay.Core.Execution;

/// <summary>Result from <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/>.</summary>
internal sealed record WorktreeFilterResult(
    IReadOnlyList<string> TrackedDiscarded,
    IReadOnlyList<string> UntrackedDeleted,
    string? Error = null);

/// <summary>
/// Reverts all worktree changes not in the declared test-files list so that
/// after stage 5 only test-file edits remain.  Production-file modifications
/// are reverted to HEAD; untracked files outside <c>testFiles</c> are deleted.
/// Compile stubs are unnecessary — a compile failure counts as "red".
/// This filter is broader than <see cref="RedGate.ComputeStripSet"/>: it also
/// catches production edits to files outside the manifest and removes
/// untracked files not listed in testFiles.
/// </summary>
internal static partial class WorktreeFilter
{
    private static readonly string[] InternalArtifactPrefixes =
        [".relay/", ".relay-scratch/", ".swival/"];

    /// <summary>
    /// Normalize a repo-relative path: strip leading <c>+</c>, replace <c>\</c>
    /// with <c>/</c>, trim leading <c>./</c> or <c>/</c>, trim trailing <c>/</c>.
    /// </summary>
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
    /// in <paramref name="testFiles"/>.
    /// </summary>
    internal static async Task<WorktreeFilterResult> DiscardNonTestEditsAsync(
        string rootPath,
        IReadOnlyList<string> testFiles,
        string? tasksDir,
        CancellationToken cancellationToken)
    {
        // Host-gated path comparer (Defect D): OrdinalIgnoreCase on macOS/Windows.
        var pathComparer = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var testSet = new HashSet<string>(
            testFiles.Select(NormalizeRepoRelativePath),
            pathComparer);

        // 0. Pre-check: inside a git worktree?
        var insideResult = await GitAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (insideResult.ExitCode != 0 || insideResult.TimedOut
            || !string.Equals(insideResult.Output.Trim(), "true", StringComparison.Ordinal))
            return new WorktreeFilterResult([], []);

        // 1. Enumerate dirty tracked files (combine unstaged + staged diffs).
        //    -c core.quotePath=false disables octal quoting of non-ASCII paths (Defect C).
        var unstagedDiff = await GitAsync(rootPath,
            ["-c", "core.quotePath=false", "diff", "--name-only"], cancellationToken);
        if (unstagedDiff.ExitCode != 0 || unstagedDiff.TimedOut)
            return new WorktreeFilterResult([], [], "git diff --name-only failed");

        // --name-status -M so staged renames expose both old and new names.
        var stagedDiff = await GitAsync(rootPath,
            ["-c", "core.quotePath=false", "diff", "--cached", "--name-status", "-M"], cancellationToken);
        if (stagedDiff.ExitCode != 0 || stagedDiff.TimedOut)
            return new WorktreeFilterResult([], [], "git diff --cached --name-status failed");

        var dirtyTracked = new List<string>();
        // Defect A: track rename pairs so both endpoints are excluded when either
        // is a testFile.  A flat list (not a host-gated dictionary) so a case-only
        // rename (Foo.cs ↔ foo.cs) does not collapse into one self-referential
        // entry under OrdinalIgnoreCase — see ComputeRenameExclusions (Hole 1).
        var renamePairs = new List<(string Old, string New)>();

        void AddLines(string output, List<string> target)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length > 0) target.Add(t);
            }
        }

        void AddNameStatusLines(string output, List<string> target)
        {
            // Parses `git diff --cached --name-status -M` output.
            // Format: <status>\t<path>  or  <status>\t<old>\t<new> (rename).
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var parts = trimmed.Split('\t');
                if (parts.Length < 2) continue;
                if (parts[0].Length > 0 && parts[0][0] == 'R' && parts.Length >= 3)
                {
                    var oldName = parts[1].Trim();
                    var newName = parts[2].Trim();
                    if (oldName.Length > 0) target.Add(oldName);
                    if (newName.Length > 0) target.Add(newName);
                    if (oldName.Length > 0 && newName.Length > 0)
                        renamePairs.Add((oldName, newName));
                }
                else
                {
                    var path = parts[1].Trim();
                    if (path.Length > 0) target.Add(path);
                }
            }
        }

        AddLines(unstagedDiff.Output, dirtyTracked);
        AddNameStatusLines(stagedDiff.Output, dirtyTracked);

        // Also include deleted tracked files.
        var deletedResult = await GitAsync(rootPath,
            ["-c", "core.quotePath=false", "ls-files", "--deleted"], cancellationToken);
        if (deletedResult.ExitCode != 0 || deletedResult.TimedOut)
            return new WorktreeFilterResult([], [], "git ls-files --deleted failed");
        AddLines(deletedResult.Output, dirtyTracked);

        // 1b. Untracked files.
        var untrackedResult = await GitAsync(rootPath,
            ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard"], cancellationToken);
        if (untrackedResult.ExitCode != 0 || untrackedResult.TimedOut)
            return new WorktreeFilterResult([], [], "git ls-files --others failed");
        var dirtyUntracked = new List<string>();
        AddLines(untrackedResult.Output, dirtyUntracked);

        // Defect A: exclude rename-pair endpoints when either touches a testFile.
        if (renamePairs.Count > 0)
        {
            var exclude = ComputeRenameExclusions(renamePairs, testSet, pathComparer);
            if (exclude.Count > 0)
                dirtyTracked = dirtyTracked.Where(p => !exclude.Contains(p)).ToList();
        }

        // 2. Separate testFiles from non-test-files.
        var nonTestTracked = dirtyTracked
            .Where(p => !testSet.Contains(NormalizeRepoRelativePath(p))
                && !IsInternalArtifact(p)
                && !IsUnderTasksDir(rootPath, p, tasksDir))
            .Distinct(pathComparer).ToList();

        var nonTestUntracked = dirtyUntracked
            .Where(p => !testSet.Contains(NormalizeRepoRelativePath(p))
                && !IsInternalArtifact(p)
                && !IsUnderTasksDir(rootPath, p, tasksDir))
            .Distinct(pathComparer).ToList();

        // Defect F: accumulate ALL errors from both phases; never early-return.
        var allErrors = new List<string>();
        var trackedDiscarded = new List<string>();

        // 3. Revert dirty tracked files to HEAD.
        foreach (var rel in nonTestTracked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkoutResult = await GitAsync(rootPath, ["checkout", "HEAD", "--", rel], cancellationToken);
            if (checkoutResult.ExitCode == 0 && !checkoutResult.TimedOut)
            { trackedDiscarded.Add(rel); continue; }

            // Defect B: checkout failed — probe whether path is in HEAD.
            // TimedOut is a hard error — never delete on timeout.
            if (checkoutResult.TimedOut)
            { allErrors.Add($"{rel}: checkout timed out"); continue; }

            var probeResult = await GitAsync(rootPath, ["cat-file", "-e", $"HEAD:{rel}"], cancellationToken);
            if (probeResult.TimedOut)
            { allErrors.Add($"{rel}: cat-file -e probe timed out"); continue; }

            if (probeResult.ExitCode == 0)
            {
                // Path IS in HEAD — checkout failure was transient. Do NOT delete.
                allErrors.Add($"{rel}: checkout failed (exit {checkoutResult.ExitCode}) but path is in HEAD — not deleted");
                continue;
            }

            // Path genuinely absent from HEAD (staged rename destination, new staged file).
            // Defect E: --ignore-unmatch so exit-128 on an absent index entry is benign (exit 0).
            // Hole 2: -f forces the unstage of an `AM` entry (a new file staged then
            // edited again — "staged content different ... use -f", exit 1 WITHOUT -f),
            // which --ignore-unmatch does NOT mask.  Without -f that exit-1 was swallowed
            // and File.Delete ran anyway, leaving the index staging a missing blob.
            var rmResult = await GitAsync(rootPath,
                ["rm", "-f", "--cached", "--ignore-unmatch", "--", rel], cancellationToken);
            if (rmResult.TimedOut)
            { allErrors.Add($"{rel}: rm --cached timed out"); continue; }

            // Hole 2: a non-zero exit here is a genuine unstage failure (the benign
            // absent-pathspec case is already zeroed by --ignore-unmatch).  Do NOT
            // delete the worktree file — that would leave the index inconsistent
            // (staging a blob for a now-missing file) with no signal.  Flag instead.
            if (rmResult.ExitCode != 0)
            { allErrors.Add($"{rel}: rm -f --cached failed (exit {rmResult.ExitCode}) — not deleted to avoid inconsistent index"); continue; }

            // Defect F: wrap File.Delete in try/catch.
            var fullPath = Path.Combine(rootPath, rel);
            try { if (File.Exists(fullPath)) File.Delete(fullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { allErrors.Add($"{rel}: File.Delete failed: {ex.Message}"); continue; }

            trackedDiscarded.Add(rel);
        }

        // 4. Delete untracked non-test files.
        var untrackedDeleted = new List<string>();
        foreach (var rel in nonTestUntracked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.Combine(rootPath, rel);
            try { if (File.Exists(full)) File.Delete(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { allErrors.Add($"{rel}: File.Delete (untracked) failed: {ex.Message}"); continue; }
            untrackedDeleted.Add(rel);
        }

        // Remove empty leaf directories left after deletions.
        foreach (var dir in nonTestUntracked
            .Select(r => Path.GetDirectoryName(Path.Combine(rootPath, r)))
            .Where(d => d is not null).Distinct(pathComparer).OrderByDescending(d => d!.Length))
        {
            if (dir is not null && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try { Directory.Delete(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { allErrors.Add($"Directory.Delete({dir}): {ex.Message}"); }
            }
        }

        // Defect F: surface ALL accumulated errors after BOTH phases complete.
        if (allErrors.Count > 0)
            return new WorktreeFilterResult(trackedDiscarded, untrackedDeleted,
                $"revert/delete failures: {string.Join("; ", allErrors)}");

        return new WorktreeFilterResult(trackedDiscarded, untrackedDeleted);
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
        if (string.IsNullOrEmpty(tasksDir)) return false;
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
