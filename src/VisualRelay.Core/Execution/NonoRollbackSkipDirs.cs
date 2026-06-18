namespace VisualRelay.Core.Execution;

/// <summary>
/// Computes the set of nono <c>--skip-dir &lt;name&gt;</c> basenames for the
/// swival launch when the sandbox is enabled. <c>--skip-dir</c> excludes a
/// directory from nono's trust-scanning and rollback PREFLIGHT (which has a
/// fixed ~2 GiB budget) WITHOUT removing swival's read/write access to it, so
/// rollback still protects everything else (the tracked source tree).
///
/// The skip set is:
///   1. ALWAYS the VCS / VR-internal artifact dirs (<c>.git</c>, <c>.relay</c>,
///      <c>.relay-scratch</c>, <c>.swival</c>) — never rollback-relevant, and
///      some grow during a run.
///   2. PLUS each immediate child dir of the target root that BOTH contains
///      git-ignored content AND is on-disk &gt;= <see cref="RollbackSkipThresholdBytes"/>.
///
/// The git-ignored gate is essential: a large but FULLY-TRACKED source dir is
/// never skipped, so its rollback protection is preserved. This is general
/// (threshold-driven, no language/ecosystem-specific names); small repos with
/// no large ignored dirs get only the always-list (harmless if absent).
/// </summary>
internal static class NonoRollbackSkipDirs
{
    /// <summary>
    /// On-disk size at/above which an ignored top-level child dir is excluded
    /// from rollback preflight (256 MB).
    /// </summary>
    private const long RollbackSkipThresholdBytes = 256L * 1024 * 1024;

    /// <summary>
    /// VCS / VR-internal artifact dir basenames that are always skipped: never
    /// rollback-relevant, and some (e.g. <c>.relay</c>) grow during a run.
    /// </summary>
    private static readonly IReadOnlyList<string> AlwaysSkip =
        [".git", ".relay", ".relay-scratch", ".swival"];

    /// <summary>
    /// Compute the skip-dir names for <paramref name="rootPath"/>. Runs
    /// <c>git ls-files --others --ignored --exclude-standard --directory</c> to
    /// find top-level dirs with ignored content, then size-gates each. Falls
    /// back to JUST the always-list when <paramref name="gitInvoker"/> is null
    /// or the git call fails/non-zero. Never throws, never blocks the run.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ComputeAsync(
        string rootPath,
        IGitInvoker? gitInvoker,
        CancellationToken ct,
        long thresholdBytes = RollbackSkipThresholdBytes)
    {
        // Default to a real GitInvoker when none is injected. Production historically
        // constructed SwivalSubagentRunner WITHOUT a gitInvoker, which silently
        // dropped the size-gated skips (the big git-ignored dirs — e.g. a multi-GB
        // data/ tree — that actually blow nono's rollback budget) and kept only the
        // always-list. Defaulting here makes the skip computation robust regardless
        // of upstream wiring; tests inject a fake to exercise specific git outputs.
        var git = gitInvoker ?? new GitInvoker();
        var ignoredTopLevel = await GetIgnoredTopLevelDirsAsync(rootPath, git, ct);

        return Decide(
            ignoredTopLevel,
            name => DirectoryMeetsSizeThreshold(Path.Combine(rootPath, name), thresholdBytes));
    }

    /// <summary>
    /// Pure decision logic: always-list ∪ {ignored top-level dirs meeting size},
    /// deduplicated (Ordinal). A large dir NOT in <paramref name="ignoredTopLevelDirs"/>
    /// is excluded (the gate). Separated from IO so it is unit-testable without a
    /// real git repo or filesystem.
    /// </summary>
    internal static IReadOnlyList<string> Decide(
        IEnumerable<string> ignoredTopLevelDirs,
        Func<string, bool> dirMeetsSizeThreshold)
    {
        var result = new List<string>(AlwaysSkip);
        var seen = new HashSet<string>(AlwaysSkip, StringComparer.Ordinal);

        foreach (var name in ignoredTopLevelDirs)
        {
            if (string.IsNullOrEmpty(name) || seen.Contains(name))
                continue;
            if (!dirMeetsSizeThreshold(name))
                continue;

            seen.Add(name);
            result.Add(name);
        }

        return result;
    }

    /// <summary>
    /// True once the cumulative on-disk size of files under
    /// <paramref name="dirPath"/> reaches <paramref name="thresholdBytes"/>.
    /// Early-exits as soon as the running total crosses the threshold so a
    /// multi-GB tree is never fully sized. Resilient to IO errors /
    /// unauthorized access / reparse points — those are skipped, never thrown.
    /// </summary>
    internal static bool DirectoryMeetsSizeThreshold(string dirPath, long thresholdBytes)
    {
        if (thresholdBytes <= 0)
            return true;

        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(dirPath);
            if (!root.Exists)
                return false;
        }
        catch
        {
            return false;
        }

        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Skip symlinked/reparse-point dirs: following them risks cycles and
            // double-counting, and they are not part of this dir's own footprint.
            try
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 && dir != root)
                    continue;
            }
            catch
            {
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos();
            }
            catch
            {
                continue; // unauthorized / IO — skip this subtree.
            }

            foreach (var entry in entries)
            {
                try
                {
                    if (entry is DirectoryInfo subDir)
                    {
                        stack.Push(subDir);
                    }
                    else if (entry is FileInfo file)
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue; // don't count symlink targets.
                        total += file.Length;
                        if (total >= thresholdBytes)
                            return true; // early exit — never fully size a huge tree.
                    }
                }
                catch
                {
                    // Per-entry IO error (deleted mid-walk, denied) — skip it.
                }
            }
        }

        return total >= thresholdBytes;
    }

    /// <summary>
    /// Run <c>git ls-files --others --ignored --exclude-standard --directory</c>
    /// and return the distinct FIRST path components (basenames) of its entries.
    /// Returns an empty set on any failure (null invoker, non-zero exit, throw)
    /// so callers fall back to the always-list.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetIgnoredTopLevelDirsAsync(
        string rootPath, IGitInvoker gitInvoker, CancellationToken ct)
    {
        try
        {
            var result = await gitInvoker.RunAsync(
                rootPath,
                ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory"],
                ct);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                return [];

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in result.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                // Entries are dir paths (trailing '/') or, when a dir isn't fully
                // ignored, file paths. Take the first path component either way.
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                var slash = trimmed.IndexOfAny(['/', '\\']);
                var top = slash >= 0 ? trimmed[..slash] : trimmed;
                if (top.Length == 0 || !seen.Add(top))
                    continue;

                names.Add(top);
            }

            return names;
        }
        catch
        {
            return [];
        }
    }
}
