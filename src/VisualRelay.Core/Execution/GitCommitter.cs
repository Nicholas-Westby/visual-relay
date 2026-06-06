namespace VisualRelay.Core.Execution;

internal static class GitCommitter
{
    public static async Task<GitCommitResult> CommitAsync(
        string rootPath,
        string taskId,
        string taskHash,
        IReadOnlyList<string> commitMessages,
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> proofFiles,
        string? commitToken,
        CancellationToken cancellationToken)
    {
        var inside = await GitAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0)
        {
            return GitCommitResult.Failed("target root is not a git repository");
        }

        var reset = await GitAsync(rootPath, ["reset", "-q"], cancellationToken);
        if (reset.ExitCode != 0)
        {
            return GitCommitResult.Failed($"git reset failed: {reset.Output.Trim()}");
        }
        IReadOnlyList<string> manifestFilesToStage;
        try
        {
            manifestFilesToStage = await ResolveManifestFilesToStageAsync(rootPath, manifest, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return GitCommitResult.Failed(ex.Message);
        }

        if (manifestFilesToStage.Count > 0)
        {
            var add = await GitAsync(rootPath, ["add", "-A", "--", .. manifestFilesToStage], cancellationToken);
            if (add.ExitCode != 0)
            {
                return GitCommitResult.Failed($"git add failed: {add.Output.Trim()}");
            }
        }

        // Also stage every tracked file the run modified or deleted, even if the
        // stage-4 manifest never listed it (agents edit shared files — a test
        // double, a csproj — without declaring them). Stage 9 verifies the working
        // tree, so the commit must match it or committed code could reference an
        // uncommitted change and fail to build from a clean checkout.
        var addTracked = await GitAsync(rootPath, ["add", "-u"], cancellationToken);
        if (addTracked.ExitCode != 0)
        {
            return GitCommitResult.Failed($"git add -u failed: {addTracked.Output.Trim()}");
        }

        // Proof files (ledger/seals/manifest) live under .relay/, which the
        // self-hosting repo gitignores along with bulky run scratch. Force them in
        // so the Relay-Seal stays verifiable; the manifest add above stays strict so
        // a genuinely ignored source path still surfaces as an error.
        if (proofFiles.Count > 0)
        {
            var addProof = await GitAsync(rootPath, ["add", "-f", "--", .. proofFiles], cancellationToken);
            if (addProof.ExitCode != 0)
            {
                return GitCommitResult.Failed($"git add proof failed: {addProof.Output.Trim()}");
            }
        }

        string? lastError = null;
        foreach (var candidate in commitMessages)
        {
            var attemptMessage = $"{candidate}\n\nTask: {taskId}\nRelay-Seal: {taskHash}\n";
            var attemptEnv = commitToken is not null
                ? new Dictionary<string, string> { ["RELAY_COMMIT_TOKEN"] = commitToken }
                : null;
            var attempt = await GitAsync(rootPath, ["commit", "-m", attemptMessage], cancellationToken, TimeSpan.FromMinutes(2), attemptEnv);
            if (attempt.ExitCode == 0)
            {
                var sha = await GitAsync(rootPath, ["rev-parse", "HEAD"], cancellationToken);
                return sha.ExitCode == 0
                    ? GitCommitResult.Committed(sha.Output.Trim())
                    : GitCommitResult.Failed($"git rev-parse failed after commit: {sha.Output.Trim()}");
            }

            lastError = attempt.Output.Trim();
        }

        return GitCommitResult.Failed($"commit rejected: {lastError}");
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        ProcessCapture.RunAsync(
            "git",
            ["-C", rootPath, .. arguments],
            rootPath,
            timeout ?? TimeSpan.FromSeconds(30),
            cancellationToken,
            environment);

    private static async Task<IReadOnlyList<string>> ResolveManifestFilesToStageAsync(
        string rootPath,
        IReadOnlyList<string> manifest,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var relative in manifest.Distinct(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(rootPath, relative);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                files.Add(relative);
                continue;
            }

            var tracked = await GitAsync(rootPath, ["ls-files", "--", relative], cancellationToken);
            if (!string.IsNullOrWhiteSpace(tracked.Output))
            {
                files.Add(relative);
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
