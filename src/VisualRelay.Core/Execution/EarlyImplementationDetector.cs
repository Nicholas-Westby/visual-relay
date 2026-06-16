namespace VisualRelay.Core.Execution;

internal static class EarlyImplementationDetector
{
    /// <summary>
    /// Returns true when at least one IMPLEMENTATION file in the manifest already
    /// differs from its committed (HEAD) content — i.e. the agent front-loaded the
    /// change into an earlier stage. Returns FALSE (the safe default) when the root
    /// is not a git work tree, when HEAD is unavailable, when the manifest has no
    /// impl files, or on any git error. Authored test files (per the
    /// <paramref name="isTestFile"/> heuristic — files under a <c>tests/</c>
    /// directory or whose names match <c>*.tests.*</c>, <c>*_test.*</c>, or
    /// <c>*.spec.*</c>) are excluded.
    /// </summary>
    internal static async Task<bool> ImplementationAlreadyUnderwayAsync(
        string rootPath,
        IReadOnlyList<string> manifest,
        Func<string, bool> isImpl,
        CancellationToken cancellationToken,
        Func<string, bool>? isTestFile = null,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var implFiles = manifest
            .Select(p => p.StartsWith('+') ? p[1..] : p) // manifest may carry '+' new-file prefix
            .Where(isImpl)
            .Where(f => isTestFile == null || !isTestFile(f))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (implFiles.Count == 0) return false;

        // Must be inside a git work tree, else we have no HEAD baseline → safe-off.
        var inside = await gi.RunAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0 || !inside.Output.Trim().StartsWith("true", StringComparison.Ordinal))
            return false;

        // `git diff --quiet HEAD -- <impl files>` exits 1 when any listed path differs
        // from HEAD (tracked-and-modified). New (untracked) files do not show here; they
        // are handled by the untracked check below. Exit 0 = clean, 1 = differs.
        var diffArgs = new List<string> { "diff", "--quiet", "HEAD", "--" };
        diffArgs.AddRange(implFiles);
        var diff = await gi.RunAsync(rootPath, diffArgs, cancellationToken);
        if (diff.ExitCode == 1) return true;
        if (diff.ExitCode != 0) return false; // any other code (e.g. no HEAD yet) → safe-off

        // An impl file the agent CREATED early is untracked, not "modified vs HEAD".
        // Detect new impl files that already exist on disk and are untracked.
        var untrackedArgs = new List<string> { "ls-files", "--others", "--exclude-standard", "--" };
        untrackedArgs.AddRange(implFiles);
        var untracked = await gi.RunAsync(rootPath, untrackedArgs, cancellationToken);
        if (untracked.ExitCode == 0 &&
            untracked.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Any())
            return true;

        return false;
    }
}
