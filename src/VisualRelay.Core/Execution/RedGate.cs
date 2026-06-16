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

    public static IReadOnlyList<string> ComputeStripSet(IReadOnlyList<string> manifest, IReadOnlyList<string> testFiles)
    {
        var tests = testFiles.ToHashSet(StringComparer.Ordinal);
        return manifest.Where(file => !tests.Contains(file)).ToArray();
    }

    public static string StashTag(string taskId, string nonce) => $"{TagPrefix}:{taskId}:{nonce}";

    public static async Task<bool> StripToRedAsync(
        string rootPath,
        IReadOnlyList<string> stripSet,
        string tag,
        CancellationToken cancellationToken,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
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
        if (await FindStashRefAsync(rootPath, tag, cancellationToken, gi) is not null)
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
        CancellationToken cancellationToken,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
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
        CancellationToken cancellationToken,
        IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var reference = await FindStashRefAsync(rootPath, match, cancellationToken, gi);
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
        string rootPath, string tag, CancellationToken ct, IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        if (!await IsGitRepositoryAsync(rootPath, gi, ct)) return false;
        var dirty = await GitAsync(gi, rootPath, ["status", "--porcelain"], ct);
        if (string.IsNullOrWhiteSpace(dirty.Output)) return false;
        await GitAsync(gi, rootPath, ["stash", "push", "-u", "-m", tag], ct);
        return await FindStashRefAsync(rootPath, tag, ct, gi) is not null;
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
