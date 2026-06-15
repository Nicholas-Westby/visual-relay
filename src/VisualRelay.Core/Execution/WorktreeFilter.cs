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
        //    -z emits NUL-delimited paths that are NEVER C-quoted (so a TAB or
        //    newline in a path stays literal — leak 1) and NEVER whitespace-
        //    trimmed (so a leading/trailing space survives — leak 2).  This
        //    supersedes -c core.quotePath=false (Defect C): with -z there is no
        //    quoting to disable.
        var unstagedDiff = await GitAsync(rootPath,
            ["diff", "--name-only", "-z"], cancellationToken);
        if (unstagedDiff.ExitCode != 0 || unstagedDiff.TimedOut)
            return new WorktreeFilterResult([], [], "git diff --name-only failed");

        // --name-status -M -C -z so staged renames AND copies expose both old
        // and new names.  -M forces rename detection ON even when the target
        // repo sets diff.renames=false (preserving the Defect-A rename-pair
        // guard); -C additionally SURFACES copies as C records (review
        // confirmed -M alone suppresses copy detection).  Renames and copies
        // are then treated DIFFERENTLY in AddNameStatusNul: a rename's source
        // is deleted so its destination is rename-protected (renamePairs); a
        // copy leaves its source intact so its destination is a plain staged
        // addition reverted/deleted like any other (NOT protected — B-1).  -z
        // makes the stream a flat sequence of NUL-separated tokens
        // (status\0path\0, or status\0old\0new\0 for R/C — no embedded TAB; see
        // the CR/CRLF limitation noted on SplitNulRecords / AddNameStatusNul).
        var stagedDiff = await GitAsync(rootPath,
            ["diff", "--cached", "--name-status", "-M", "-C", "-z"], cancellationToken);
        if (stagedDiff.ExitCode != 0 || stagedDiff.TimedOut)
            return new WorktreeFilterResult([], [], "git diff --cached --name-status failed");

        var dirtyTracked = new List<string>();
        // Defect A: track rename pairs so both endpoints are excluded when either
        // is a testFile.  A flat list (not a host-gated dictionary) so a case-only
        // rename (Foo.cs ↔ foo.cs) does not collapse into one self-referential
        // entry under OrdinalIgnoreCase — see ComputeRenameExclusions (Hole 1).
        var renamePairs = new List<(string Old, string New)>();

        // NUL-delimited parsing (AddNulPaths / AddNameStatusNul) lives in the
        // WorktreeFilter.Parsing.cs partial — it splits on \0 and never trims,
        // fixing the quoted-path (leak 1) and whitespace-path (leak 2) misses.
        AddNulPaths(unstagedDiff.Output, dirtyTracked);
        AddNameStatusNul(stagedDiff.Output, dirtyTracked, renamePairs);

        // Also include deleted tracked files (-z: NUL-delimited, never quoted).
        var deletedResult = await GitAsync(rootPath,
            ["ls-files", "--deleted", "-z"], cancellationToken);
        if (deletedResult.ExitCode != 0 || deletedResult.TimedOut)
            return new WorktreeFilterResult([], [], "git ls-files --deleted failed");
        AddNulPaths(deletedResult.Output, dirtyTracked);

        // 1b. Untracked files (-z: NUL-delimited, never quoted).
        var untrackedResult = await GitAsync(rootPath,
            ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        if (untrackedResult.ExitCode != 0 || untrackedResult.TimedOut)
            return new WorktreeFilterResult([], [], "git ls-files --others failed");
        var dirtyUntracked = new List<string>();
        AddNulPaths(untrackedResult.Output, dirtyUntracked);

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
            if (checkoutResult is { ExitCode: 0, TimedOut: false })
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
