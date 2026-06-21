namespace VisualRelay.Core.Execution;

internal static partial class GitCommitter
{
    public static async Task<GitCommitResult> CommitAsync(
        string rootPath,
        string taskId,
        string taskHash,
        IReadOnlyList<string> commitMessages,
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> proofFiles,
        string? commitToken,
        IReadOnlySet<string>? preRunUntracked,
        string? tasksDir,
        CancellationToken cancellationToken = default,
        IGitInvoker? gitInvoker = null,
        string? runBaseSha = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var inside = await GitAsync(gi, rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0)
        {
            return GitCommitResult.Failed($"target root is not a git repository (git exit {inside.ExitCode}): {inside.Output.Trim()}");
        }

        // Squash any commits the agent made itself during the run (authorized via
        // RELAY_COMMIT_TOKEN, so they land BARE — no Task:/Relay-Seal: trailers)
        // into the single sealed commit below: soft-reset to the run-base so every
        // change since run-start stays staged with the run-base as parent. No-op
        // when HEAD is already the run-base (the normal path) or when the range
        // holds a sealed commit (never rewind another task's seal). See .Squash.cs.
        var squash = await SquashInRunCommitsAsync(gi, rootPath, runBaseSha, cancellationToken);
        if (squash.Failure is not null)
            return squash.Failure;
        // Non-null only when a soft-reset rewound HEAD: the pre-squash sha to
        // restore on any failure path below so the agent's commits are not lost.
        var preSquashHead = squash.OrigHead;

        // FIX 2: ANY failure return after the squash rewound HEAD must first restore
        // HEAD to its pre-squash sha — otherwise the agent's self-commits exist only
        // in the (soon-discarded) index and are lost. Route every post-squash
        // failure through this so the rollback is total, not just the all-rejected
        // path. When no squash happened (preSquashHead is null) this is a byte-for-
        // byte no-op: it returns the same Failed result and touches nothing.
        async Task<GitCommitResult> FailAsync(string error)
        {
            if (preSquashHead is not null)
                await RestoreHeadAfterFailedSquashAsync(gi, rootPath, preSquashHead, cancellationToken);
            return GitCommitResult.Failed(error);
        }

        var reset = await GitAsync(gi, rootPath, ["reset", "-q"], cancellationToken);
        if (reset.ExitCode != 0)
        {
            return await FailAsync($"git reset failed (git exit {reset.ExitCode}): {reset.Output.Trim()}");
        }
        IReadOnlyList<string> manifestFilesToStage;
        try
        {
            manifestFilesToStage = await ResolveManifestFilesToStageAsync(gi, rootPath, manifest, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return await FailAsync(ex.Message);
        }

        // Pre-check: reject gitignored manifest entries before git add buries
        // the path names in a multi-line hint.
        if (manifest.Count > 0)
        {
            var checkArgs = new List<string> { "check-ignore", "--" };
            checkArgs.AddRange(manifest);
            var ci = await GitAsync(gi, rootPath, checkArgs, cancellationToken);
            if (ci.ExitCode == 0 && !string.IsNullOrWhiteSpace(ci.Output))
            {
                var ignored = ci.Output.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                var q = string.Join(", ", ignored.Select(p => $"`{p}`"));
                return await FailAsync(
                    ignored.Length == 1
                        ? $"manifest contains gitignored path: {q}"
                        : $"manifest contains gitignored paths: {q}");
            }
        }

        if (manifestFilesToStage.Count > 0)
        {
            var add = await GitAsync(gi, rootPath, ["add", "-A", "--", .. manifestFilesToStage], cancellationToken);
            if (add.ExitCode != 0)
            {
                return await FailAsync($"git add failed (git exit {add.ExitCode}): {add.Output.Trim()}");
            }
        }

        // Also stage every tracked file the run modified or deleted, even if the
        // stage-4 manifest never listed it (agents edit shared files — a test
        // double, a csproj — without declaring them). Stage 9 verifies the working
        // tree, so the commit must match it or committed code could reference an
        // uncommitted change and fail to build from a clean checkout.
        var addTracked = await GitAsync(gi, rootPath, ["add", "-u"], cancellationToken);
        if (addTracked.ExitCode != 0)
        {
            return await FailAsync($"git add -u failed (git exit {addTracked.ExitCode}): {addTracked.Output.Trim()}");
        }

        // Proof files (ledger/seals/manifest) live under .relay/, which the
        // self-hosting repo gitignores along with bulky run scratch. Force them in
        // so the Relay-Seal stays verifiable; the manifest add above stays strict so
        // a genuinely ignored source path still surfaces as an error.
        if (proofFiles.Count > 0)
        {
            var addProof = await GitAsync(gi, rootPath, ["add", "-f", "--", .. proofFiles], cancellationToken);
            if (addProof.ExitCode != 0)
            {
                return await FailAsync($"git add proof failed (git exit {addProof.ExitCode}): {addProof.Output.Trim()}");
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
            var currentUntracked = await CaptureUntrackedSnapshotAsync(rootPath, cancellationToken, gi);
            var newAuthored = new List<string>();
            foreach (var path in currentUntracked)
            {
                if (!preRunUntracked.Contains(path) && !IsInternalArtifact(path) && !IsUnderTasksDir(rootPath, path, tasksDir))
                {
                    newAuthored.Add(path);
                }
            }

            if (newAuthored.Count > 0)
            {
                var addNew = await GitAsync(gi, rootPath, ["add", "--", .. newAuthored], cancellationToken);
                if (addNew.ExitCode != 0)
                {
                    return await FailAsync($"git add auto-include failed (git exit {addNew.ExitCode}): {addNew.Output.Trim()}");
                }
            }
        }

        // FIX 3: the staging above rebuilds the index from the WORKING TREE, which
        // misses paths that exist only in the rewound commits' tree (e.g. an
        // index-only merge among the agent's self-commits, or a committed file the
        // agent later removed from the working tree without staging the delete). If
        // a squash rewound HEAD, re-stage any such committed-only path from the
        // pre-squash tree so no tracked change is silently dropped from the seal.
        if (preSquashHead is not null)
        {
            var stageFail = await StageCommittedOnlyContentAsync(gi, rootPath, runBaseSha!, preSquashHead, cancellationToken);
            if (stageFail is not null)
                return await FailAsync(stageFail.Error ?? "squash content-preservation failed");
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
            var attempt = await GitAsync(gi, rootPath, ["commit", "-m", attemptMessage], cancellationToken, TimeSpan.FromMinutes(2), attemptEnv);
            if (attempt.ExitCode == 0)
            {
                var sha = await GitAsync(gi, rootPath, ["rev-parse", "HEAD"], cancellationToken);
                return sha.ExitCode == 0
                    ? GitCommitResult.Committed(sha.Output.Trim())
                    : GitCommitResult.Failed($"git rev-parse failed after commit (git exit {sha.ExitCode}): {sha.Output.Trim()}");
            }

            lastError = $"(git exit {attempt.ExitCode}): {attempt.Output.Trim()}";
        }

        // Every candidate was rejected by a target-repo hook. FailAsync restores
        // HEAD when a squash rewound it (FIX 2), so the agent's self-commits are
        // reinstated instead of dropped on the next worktree reset.
        return await FailAsync($"commit rejected: {lastError}");
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        IGitInvoker gitInvoker,
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        const int maxAttempts = 3;
        // Materialize once: this retry loop enumerates 'arguments' on every attempt,
        // so a deferred source would re-run side effects / risk inconsistent args.
        var args = arguments as IReadOnlyList<string> ?? arguments.ToList();
        (int ExitCode, string Output, bool TimedOut) lastResult = default;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = await gitInvoker.RunAsync(rootPath, args, cancellationToken, timeout, environment);

            if (result.ExitCode == 0 || attempt == maxAttempts)
                return result;

            lastResult = result;
            var delay = attempt == 1 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, cancellationToken);
        }

        return lastResult;
    }

    private static async Task<IReadOnlyList<string>> ResolveManifestFilesToStageAsync(
        IGitInvoker gitInvoker,
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

            var tracked = await GitAsync(gitInvoker, rootPath, ["ls-files", "--", relative], cancellationToken);
            if (!string.IsNullOrWhiteSpace(tracked.Output))
            {
                files.Add(relative);
            }
        }

        return files;
    }
}
