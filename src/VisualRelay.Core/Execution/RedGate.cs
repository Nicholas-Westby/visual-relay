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
        CancellationToken cancellationToken)
    {
        if (stripSet.Count == 0 || !await IsGitRepositoryAsync(rootPath, cancellationToken))
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

        var dirty = await GitAsync(rootPath, ["status", "--porcelain", "--", .. present], cancellationToken);
        if (string.IsNullOrWhiteSpace(dirty.Output))
        {
            return false;
        }

        var stash = await GitAsync(rootPath, ["stash", "push", "-u", "-m", tag, "--", .. present], cancellationToken);
        if (await FindStashRefAsync(rootPath, tag, cancellationToken) is not null)
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
        CancellationToken cancellationToken)
    {
        var list = await GitAsync(rootPath, ["stash", "list"], cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var reference = await FindStashRefAsync(rootPath, match, cancellationToken);
        if (reference is null)
        {
            return RedGateRestoreResult.Absent;
        }

        var apply = await GitAsync(rootPath, ["stash", "apply", reference], cancellationToken);
        if (apply.ExitCode != 0)
        {
            return RedGateRestoreResult.Conflict;
        }

        await GitAsync(rootPath, ["stash", "drop", reference], cancellationToken);
        return RedGateRestoreResult.Restored;
    }

    private static async Task<bool> IsGitRepositoryAsync(string rootPath, CancellationToken cancellationToken)
    {
        var inside = await GitAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        return inside.ExitCode == 0 && inside.Output.Trim().Equals("true", StringComparison.Ordinal);
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) =>
        ProcessCapture.RunAsync("git", ["-C", rootPath, .. arguments], rootPath, TimeSpan.FromSeconds(30), cancellationToken);
}
