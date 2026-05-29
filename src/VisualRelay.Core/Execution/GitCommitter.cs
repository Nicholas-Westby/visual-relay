namespace VisualRelay.Core.Execution;

internal static class GitCommitter
{
    public static async Task<GitCommitResult> CommitAsync(
        string rootPath,
        string taskId,
        string taskHash,
        string commitMessage,
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> proofFiles,
        CancellationToken cancellationToken)
    {
        var inside = await GitAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0)
        {
            return GitCommitResult.Failed("target root is not a git repository");
        }

        await GitAsync(rootPath, ["reset", "-q"], cancellationToken);
        IReadOnlyList<string> manifestFilesToStage;
        try
        {
            manifestFilesToStage = await ResolveManifestFilesToStageAsync(rootPath, manifest, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return GitCommitResult.Failed(ex.Message);
        }

        var add = await GitAsync(rootPath, ["add", "--", .. manifestFilesToStage, .. proofFiles], cancellationToken);
        if (add.ExitCode != 0)
        {
            return GitCommitResult.Failed($"git add failed: {add.Output.Trim()}");
        }

        var message = $"{commitMessage}\n\nTask: {taskId}\nRelay-Seal: {taskHash}\n";
        var commit = await GitAsync(rootPath, ["commit", "-m", message], cancellationToken, TimeSpan.FromMinutes(2));
        if (commit.ExitCode != 0)
        {
            return GitCommitResult.Failed($"git commit failed: {commit.Output.Trim()}");
        }

        var sha = await GitAsync(rootPath, ["rev-parse", "HEAD"], cancellationToken);
        return sha.ExitCode == 0
            ? GitCommitResult.Committed(sha.Output.Trim())
            : GitCommitResult.Failed("commit landed but rev-parse failed");
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        ProcessCapture.RunAsync(
            "git",
            ["-C", rootPath, .. arguments],
            rootPath,
            timeout ?? TimeSpan.FromSeconds(30),
            cancellationToken);

    private static async Task<IReadOnlyList<string>> ResolveManifestFilesToStageAsync(
        string rootPath,
        IReadOnlyList<string> manifest,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var relative in manifest.Distinct(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(rootPath, relative);
            if (File.Exists(fullPath))
            {
                files.Add(relative);
                continue;
            }

            var tracked = await GitAsync(rootPath, ["ls-files", "--error-unmatch", "--", relative], cancellationToken);
            if (tracked.ExitCode == 0)
            {
                var remove = await GitAsync(rootPath, ["rm", "-q", "--", relative], cancellationToken);
                if (remove.ExitCode != 0)
                {
                    throw new InvalidOperationException($"git rm failed for {relative}: {remove.Output.Trim()}");
                }
            }
        }

        return files;
    }
}

internal sealed record GitCommitResult(bool Success, string? CommitSha, string? Error)
{
    public static GitCommitResult Committed(string sha) => new(true, sha, null);
    public static GitCommitResult Failed(string error) => new(false, null, error);
}
