namespace VisualRelay.Core.Execution;

public enum RedGateRestoreResult
{
    Restored,
    Conflict,
    Absent
}

public static class RedGate
{
    private const string TagPrefix = "relay-redgate";

    /// <summary>
    /// The manifest entries that are NOT declared test files — what the gate strips
    /// to make the tree red. Both sides are normalized first: the manifest comes
    /// from stage 4 and the test list from stage 5, both written by a model, so one
    /// file can arrive under two names (<c>./src/x</c> and <c>src/x</c>). Compared
    /// raw, that difference put a DECLARED TEST in the strip set and the gate then
    /// stashed the very test it was about to run.
    /// </summary>
    /// <param name="rootPath">The workspace both lists were written against.</param>
    /// <param name="manifest">The plan's manifest.</param>
    /// <param name="testFiles">The test files stage 5 declared.</param>
    /// <returns>The normalized paths to strip, never a rooted one.</returns>
    public static IReadOnlyList<string> ComputeStripSet(
        string rootPath, IReadOnlyList<string> manifest, IReadOnlyList<string> testFiles)
    {
        var tests = WorktreeFilter.NormalizeTestFileList(rootPath, testFiles).ToHashSet(StringComparer.Ordinal);
        return [.. ManifestPaths.ResolveAll(rootPath, manifest).Resolved
            .Where(file => !tests.Contains(file))];
    }

    public static string StashTag(string taskId, string nonce) => $"{TagPrefix}:{taskId}:{nonce}";

    public static async Task<bool> StripToRedAsync(
        string rootPath,
        IReadOnlyList<string> stripSet,
        string tag,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken)
    {
        var gi = gitInvoker;
        if (stripSet.Count == 0 || !await IsGitRepositoryAsync(rootPath, gi, cancellationToken))
        {
            return false;
        }

        var present = stripSet
            .Where(relative => File.Exists(Path.Combine(rootPath, relative)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (present.Length == 0)
        {
            return false;
        }

        var dirty = await GitAsync(gi, rootPath, ["status", "--porcelain", "--", .. present], cancellationToken);
        if (string.IsNullOrWhiteSpace(dirty.Output))
        {
            return false;
        }

        var stash = await GitAsync(gi, rootPath, ["stash", "push", "-u", "-m", tag, "--", .. present], cancellationToken);
        if (await FindStashRefAsync(rootPath, tag, gi, cancellationToken) is not null)
        {
            return true;
        }

        if (stash.ExitCode != 0)
        {
            throw new InvalidOperationException($"relay red gate stash failed: {stash.Output.Trim()}");
        }

        return true;
    }

    public static async Task<string?> FindStashRefAsync(
        string rootPath,
        string match,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken)
    {
        var gi = gitInvoker;
        var list = await GitAsync(gi, rootPath, ["stash", "list"], cancellationToken);
        foreach (var line in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(match, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                return line[..separator];
            }
        }

        return null;
    }

    public static async Task<RedGateRestoreResult> RestoreStashAsync(
        string rootPath,
        string match,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken)
    {
        var gi = gitInvoker;
        var reference = await FindStashRefAsync(rootPath, match, gi, cancellationToken);
        if (reference is null)
        {
            return RedGateRestoreResult.Absent;
        }

        await GitAsync(gi, rootPath, ["checkout", "--", "."], cancellationToken);
        var apply = await GitAsync(gi, rootPath, ["stash", "apply", reference], cancellationToken);
        if (apply.ExitCode != 0)
        {
            return RedGateRestoreResult.Conflict;
        }

        await GitAsync(gi, rootPath, ["stash", "drop", reference], cancellationToken);
        return RedGateRestoreResult.Restored;
    }

    public static async Task<bool> StashAllAsync(
        string rootPath, string tag, IGitInvoker gitInvoker, CancellationToken ct)
    {
        var gi = gitInvoker;
        if (!await IsGitRepositoryAsync(rootPath, gi, ct)) return false;
        var dirty = await GitAsync(gi, rootPath, ["status", "--porcelain"], ct);
        if (string.IsNullOrWhiteSpace(dirty.Output)) return false;
        await GitAsync(gi, rootPath, ["stash", "push", "-u", "-m", tag], ct);
        return await FindStashRefAsync(rootPath, tag, gi, ct) is not null;
    }

    private static async Task<bool> IsGitRepositoryAsync(string rootPath, IGitInvoker gitInvoker, CancellationToken cancellationToken)
    {
        var inside = await GitAsync(gitInvoker, rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        return inside.ExitCode == 0 && inside.Output.Trim().Equals("true", StringComparison.Ordinal);
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        IGitInvoker gitInvoker,
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) =>
        gitInvoker.RunAsync(rootPath, arguments, cancellationToken);
}
