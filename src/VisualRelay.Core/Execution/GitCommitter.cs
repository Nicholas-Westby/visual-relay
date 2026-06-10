namespace VisualRelay.Core.Execution;

internal static class GitCommitter
{
    // Visual Relay's own run artifacts. These are never auto-committed (the
    // deliberate proof subset is force-added via proofFiles); everything else the
    // run authors is fair game. Auto-include must stay repo-agnostic — it must NOT
    // assume a src/tests/tools layout, since Visual Relay runs on any repo.
    private static readonly string[] InternalArtifactPrefixes = [".relay/", ".relay-scratch/", ".swival/"];

    /// <summary>
    /// Test seam: when set, GitAsync calls this instead of the real git process.
    /// Receives (rootPath, arguments, cancellationToken, timeout, environment).
    /// When null (production), the retry-wrapped real git runner is used.
    /// </summary>
    internal static Func<string, IEnumerable<string>, CancellationToken, TimeSpan?, IReadOnlyDictionary<string, string>?, Task<(int ExitCode, string Output, bool TimedOut)>>? RawGitRunner { get; set; }

    public static async Task<GitCommitResult> CommitAsync(
        string rootPath,
        string taskId,
        string taskHash,
        IReadOnlyList<string> commitMessages,
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> proofFiles,
        string? commitToken,
        IReadOnlySet<string>? preRunUntracked,
        CancellationToken cancellationToken)
    {
        var inside = await GitAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0)
        {
            return GitCommitResult.Failed($"target root is not a git repository (git exit {inside.ExitCode}): {inside.Output.Trim()}");
        }

        var reset = await GitAsync(rootPath, ["reset", "-q"], cancellationToken);
        if (reset.ExitCode != 0)
        {
            return GitCommitResult.Failed($"git reset failed (git exit {reset.ExitCode}): {reset.Output.Trim()}");
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
                return GitCommitResult.Failed($"git add failed (git exit {add.ExitCode}): {add.Output.Trim()}");
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
            return GitCommitResult.Failed($"git add -u failed (git exit {addTracked.ExitCode}): {addTracked.Output.Trim()}");
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
                return GitCommitResult.Failed($"git add proof failed (git exit {addProof.ExitCode}): {addProof.Output.Trim()}");
            }
        }

        // Auto-include every new untracked file the run authored that the stage-4
        // manifest never listed. git add -u only stages tracked modifications; a
        // brand-new file has no tracked ancestor and is skipped. We diff the
        // run-start snapshot against the current untracked set (so pre-existing
        // scratch is left alone) and stage the delta. git's --exclude-standard
        // already drops .gitignored paths, and we additionally skip Visual Relay's
        // own artifact dirs. No source-root allowlist: Visual Relay targets any
        // repo layout (Python, JS, Go, root-level code, …), so a run-authored file
        // outside src/tests/tools must not be silently dropped either.
        if (preRunUntracked is not null)
        {
            var currentUntracked = await CaptureUntrackedSnapshotAsync(rootPath, cancellationToken);
            var newAuthored = new List<string>();
            foreach (var path in currentUntracked)
            {
                if (!preRunUntracked.Contains(path) && !IsInternalArtifact(path))
                {
                    newAuthored.Add(path);
                }
            }

            if (newAuthored.Count > 0)
            {
                var addNew = await GitAsync(rootPath, ["add", "--", .. newAuthored], cancellationToken);
                if (addNew.ExitCode != 0)
                {
                    return GitCommitResult.Failed($"git add auto-include failed (git exit {addNew.ExitCode}): {addNew.Output.Trim()}");
                }
            }
        }

        string? lastError = null;
        foreach (var candidate in commitMessages)
        {
            var attemptMessage = $"{candidate}\n\nTask: {taskId}\nRelay-Seal: {taskHash}\n";
            // Authorize this commit past the active-run pre-commit guard. Visual
            // Relay's own hook reads RELAY_COMMIT_TOKEN; the original Relay's hook
            // (e.g. JobFinder's .relay/hooks/pre-commit.ts) reads RELAY_NONCE. Both
            // are the active-lock nonce, so set both for cross-relay compatibility —
            // otherwise Visual Relay can never land a sealed commit in a repo whose
            // guard expects RELAY_NONCE.
            var attemptEnv = commitToken is not null
                ? new Dictionary<string, string>
                {
                    ["RELAY_COMMIT_TOKEN"] = commitToken,
                    ["RELAY_NONCE"] = commitToken,
                }
                : null;
            var attempt = await GitAsync(rootPath, ["commit", "-m", attemptMessage], cancellationToken, TimeSpan.FromMinutes(2), attemptEnv);
            if (attempt.ExitCode == 0)
            {
                var sha = await GitAsync(rootPath, ["rev-parse", "HEAD"], cancellationToken);
                return sha.ExitCode == 0
                    ? GitCommitResult.Committed(sha.Output.Trim())
                    : GitCommitResult.Failed($"git rev-parse failed after commit (git exit {sha.ExitCode}): {sha.Output.Trim()}");
            }

            lastError = $"(git exit {attempt.ExitCode}): {attempt.Output.Trim()}";
        }

        return GitCommitResult.Failed($"commit rejected: {lastError}");
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        const int maxAttempts = 3;
        (int ExitCode, string Output, bool TimedOut) lastResult = default;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = RawGitRunner is not null
                ? await RawGitRunner(rootPath, arguments, cancellationToken, timeout, environment)
                : await RunGitCoreAsync(rootPath, arguments, cancellationToken, timeout, environment);

            if (result.ExitCode == 0 || attempt == maxAttempts)
                return result;

            lastResult = result;
            var delay = attempt == 1 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, cancellationToken);
        }

        return lastResult;
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> RunGitCoreAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout,
        IReadOnlyDictionary<string, string>? environment) =>
        GitInvoker.RunAsync(
            rootPath,
            arguments,
            cancellationToken,
            timeout,
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

    /// <summary>
    /// Captures the set of untracked, non-ignored files at the start of a run.
    /// Uses <c>git ls-files --others --exclude-standard</c>, which respects
    /// <c>.gitignore</c>, <c>.git/info/exclude</c>, and the global gitignore.
    /// </summary>
    public static async Task<IReadOnlySet<string>> CaptureUntrackedSnapshotAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var result = await GitAsync(rootPath, ["ls-files", "--others", "--exclude-standard"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed: {result.Output.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var lines = result.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }

    private static bool IsInternalArtifact(string relativePath)
    {
        foreach (var prefix in InternalArtifactPrefixes)
        {
            if (relativePath.StartsWith(prefix, StringComparison.Ordinal)
                || string.Equals(relativePath, prefix.TrimEnd('/'), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Post-commit invariant check: returns any untracked, non-internal files
    /// that are absent from <paramref name="preRunUntracked"/> — i.e. files the
    /// run authored but the commit did not stage. An empty result means the
    /// commit captured everything the run produced.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindUncommittedAuthoredFilesAsync(
        string rootPath,
        IReadOnlySet<string> preRunUntracked,
        CancellationToken cancellationToken)
    {
        var currentUntracked = await CaptureUntrackedSnapshotAsync(rootPath, cancellationToken);
        var missed = new List<string>();
        foreach (var path in currentUntracked)
        {
            if (!preRunUntracked.Contains(path) && !IsInternalArtifact(path))
            {
                missed.Add(path);
            }
        }

        return missed;
    }
}

internal sealed record GitCommitResult(bool Success, string? CommitSha, string? Error)
{
    public static GitCommitResult Committed(string sha) => new(true, sha, null);
    public static GitCommitResult Failed(string error) => new(false, null, error);
}
