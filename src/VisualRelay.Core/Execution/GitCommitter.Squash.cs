namespace VisualRelay.Core.Execution;

internal static partial class GitCommitter
{
    /// <summary>
    /// Outcome of <see cref="SquashInRunCommitsAsync"/>.
    /// <list type="bullet">
    /// <item><see cref="Failure"/> — a hard failure the caller must return as-is
    ///   (the reset itself failed); when non-null, no reset has taken effect.</item>
    /// <item><see cref="OrigHead"/> — the HEAD sha captured BEFORE a successful
    ///   soft-reset, so the caller can restore it on any later failure path
    ///   (e.g. every candidate message rejected by a target-repo hook). Null when
    ///   no reset happened (the normal no-self-commit path, or a skipped squash).</item>
    /// </list>
    /// </summary>
    private readonly record struct SquashOutcome(GitCommitResult? Failure, string? OrigHead)
    {
        /// <summary>No-op / skip: nothing was rewound, caller proceeds normally.</summary>
        public static readonly SquashOutcome NoOp = new(null, null);
    }

    /// <summary>
    /// The trailer a sealed Visual Relay commit carries. Any commit bearing it is
    /// finished, attributed work that must never be rewound by another task's
    /// squash. Kept in lock-step with the seal written in <c>CommitAsync</c>.
    /// </summary>
    private const string SealTrailerKey = "Relay-Seal:";

    /// <summary>
    /// Soft-resets HEAD back to <paramref name="runBaseSha"/> so any commits the
    /// agent made during the run are un-committed but their changes stay staged
    /// (via the index), to be folded into the single sealed commit. Returns
    /// <see cref="SquashOutcome.NoOp"/> when there is nothing to do (no run-base
    /// supplied, run-base is HEAD, run-base is not an ancestor of HEAD, or the
    /// <c>runBase..HEAD</c> range contains an already-SEALED commit), or a
    /// failure <see cref="GitCommitResult"/> when the reset itself fails.
    ///
    /// On a successful reset the returned <see cref="SquashOutcome.OrigHead"/> is
    /// the pre-reset HEAD: the caller MUST restore it on any subsequent failure
    /// path so the agent's self-commits are not lost (see FIX 2 in CommitAsync).
    /// </summary>
    private static async Task<SquashOutcome> SquashInRunCommitsAsync(
        IGitInvoker gi,
        string rootPath,
        string? runBaseSha,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runBaseSha))
            return SquashOutcome.NoOp;

        // Resolve the run-base to a full sha. If it can't be resolved (the
        // recorded sha is gone, e.g. an unrelated history rewrite), do nothing
        // rather than risk a destructive reset to a bad ref.
        var baseRev = await GitAsync(gi, rootPath, ["rev-parse", "--verify", "--quiet", $"{runBaseSha}^{{commit}}"], cancellationToken);
        if (baseRev.ExitCode != 0 || string.IsNullOrWhiteSpace(baseRev.Output))
            return SquashOutcome.NoOp;
        var resolvedBase = baseRev.Output.Trim();

        var headRev = await GitAsync(gi, rootPath, ["rev-parse", "HEAD"], cancellationToken);
        if (headRev.ExitCode != 0)
            return SquashOutcome.NoOp;
        var head = headRev.Output.Trim();

        // HEAD already at run-base → no in-run commits to squash. This is the
        // normal path when the agent did not self-commit; behaviour unchanged.
        if (string.Equals(head, resolvedBase, StringComparison.Ordinal))
            return SquashOutcome.NoOp;

        // Only squash when the run-base is a strict ancestor of HEAD. If it is
        // not (detached/diverged HEAD, or a bad recorded sha), resetting would
        // discard or rewrite unrelated history — skip and let the working-tree
        // commit proceed against the current HEAD.
        var ancestor = await GitAsync(gi, rootPath, ["merge-base", "--is-ancestor", resolvedBase, head], cancellationToken);
        if (ancestor.ExitCode != 0)
            return SquashOutcome.NoOp;

        // FIX 1 (CRITICAL): strict-ancestor proves no-divergence, NOT
        // no-intervening-SEALED-work. A task resumed against a STALE run-base
        // could have a later task's sealed commit sitting in runBase..HEAD; a
        // reset across it would fold that sealed work + provenance into the wrong
        // seal and destroy it. So require EVERY commit in the range to be a bare
        // agent self-commit (no Relay-Seal: trailer). If any is sealed, abort the
        // squash — a cosmetic double-commit is strictly better than a lost seal.
        if (await RangeContainsSealedCommitAsync(gi, rootPath, resolvedBase, head, cancellationToken))
            return SquashOutcome.NoOp;

        // Soft reset: keeps every change since the run-base staged (the index
        // still holds HEAD's full tree), drops the agent's bare commits, repoints
        // HEAD at the run-base. Capture HEAD first so the caller can restore it if
        // the commit is ultimately rejected (FIX 2).
        var reset = await GitAsync(gi, rootPath, ["reset", "--soft", resolvedBase], cancellationToken);
        if (reset.ExitCode != 0)
            return new SquashOutcome(GitCommitResult.Failed($"git reset --soft to run-base failed (git exit {reset.ExitCode}): {reset.Output.Trim()}"), null);

        return new SquashOutcome(null, head);
    }

    /// <summary>
    /// Restores HEAD to <paramref name="origHead"/> via a soft reset, reinstating
    /// the agent's self-commits after a squash whose seal commit was then rejected
    /// (FIX 2). Best-effort: a failure here is only logged into the returned error
    /// since the caller is already on a failure path.
    /// </summary>
    private static async Task RestoreHeadAfterFailedSquashAsync(
        IGitInvoker gi,
        string rootPath,
        string origHead,
        CancellationToken cancellationToken)
    {
        // Soft reset back to the pre-squash HEAD: re-creates the dropped commits
        // and leaves the index as the seal attempt left it (harmless — the next
        // worktree reset rebuilds it). Without this the rewound commits are lost.
        await GitAsync(gi, rootPath, ["reset", "--soft", origHead], cancellationToken);
    }

    /// <summary>
    /// FIX 3: stages, from the pre-squash committed tree, any path present in
    /// <c>runBase..origHead</c> that the working-tree-based staging missed. After a
    /// squash soft-reset and the caller's mixed <c>git reset -q</c>, the index is
    /// rebuilt from the working tree; a path that exists only in the rewound
    /// commits' tree (never in the working tree — e.g. an index-only merge, or a
    /// committed file later deleted from the working tree without staging) would be
    /// dropped from the seal. We diff <paramref name="runBaseSha"/>..<paramref
    /// name="origHead"/> for added/modified paths and, for each that is NOT already
    /// in the index, restore its committed version into the index ONLY (the working
    /// tree is untouched, so a genuine working-tree edit already staged by
    /// <c>git add -u</c> is never overridden). Returns a failure result on a hard
    /// git error, else <c>null</c>.
    /// </summary>
    private static async Task<GitCommitResult?> StageCommittedOnlyContentAsync(
        IGitInvoker gi,
        string rootPath,
        string runBaseSha,
        string origHead,
        CancellationToken cancellationToken)
    {
        // Paths added (A) or modified (M) in the rewound commits, relative to the
        // run-base. Deletions (D) are excluded: a path the agent committed as
        // deleted must STAY deleted, and restoring it would resurrect dead content.
        var diff = await GitAsync(gi, rootPath,
            ["diff", "--name-only", "--diff-filter=AM", "-z", runBaseSha, origHead], cancellationToken);
        if (diff.ExitCode != 0)
            return GitCommitResult.Failed($"git diff for squash content-preservation failed (git exit {diff.ExitCode}): {diff.Output.Trim()}");
        if (string.IsNullOrEmpty(diff.Output))
            return null;

        foreach (var path in diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var rel = path.Trim();
            if (rel.Length == 0)
                continue;

            // Already represented in the index (staged from the working tree, the
            // authoritative current content) → leave it; do not clobber a live edit.
            var staged = await GitAsync(gi, rootPath, ["ls-files", "--", rel], cancellationToken);
            if (staged.ExitCode == 0 && !string.IsNullOrWhiteSpace(staged.Output))
                continue;

            // Committed-only path missed by working-tree staging: restore its
            // pre-squash version into the index only (no working-tree change).
            var restore = await GitAsync(gi, rootPath,
                ["restore", "--staged", $"--source={origHead}", "--", rel], cancellationToken);
            if (restore.ExitCode != 0)
                return GitCommitResult.Failed($"git restore for squash content-preservation failed (git exit {restore.ExitCode}): {restore.Output.Trim()}");
        }

        return null;
    }

    /// <summary>
    /// True when any commit in <c>base..head</c> carries a <see cref="SealTrailerKey"/>
    /// trailer — i.e. a finished, sealed Visual Relay commit that must not be
    /// rewound. Enumerates the range and inspects each commit's full message.
    /// On any git failure returns <c>true</c> (fail-safe: do NOT squash if we
    /// cannot prove the range is seal-free).
    /// </summary>
    private static async Task<bool> RangeContainsSealedCommitAsync(
        IGitInvoker gi,
        string rootPath,
        string baseSha,
        string head,
        CancellationToken cancellationToken)
    {
        var revs = await GitAsync(gi, rootPath, ["rev-list", $"{baseSha}..{head}"], cancellationToken);
        if (revs.ExitCode != 0)
            return true; // cannot enumerate — assume unsafe, skip the squash.
        if (string.IsNullOrWhiteSpace(revs.Output))
            return false; // empty range (shouldn't happen here) — nothing sealed.

        foreach (var line in revs.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var sha = line.Trim();
            if (sha.Length == 0)
                continue;

            // Inspect this single commit's full message body for the seal trailer.
            // %B yields subject + body, which is where the trailer lives.
            var body = await GitAsync(gi, rootPath, ["log", "-1", "--format=%B", sha], cancellationToken);
            if (body.ExitCode != 0)
                return true; // cannot read this commit — assume unsafe.
            if (CommitBodyHasSealTrailer(body.Output))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a commit message body contains a <see cref="SealTrailerKey"/>
    /// trailer line. Matches the trailer at the start of any line (after
    /// trimming leading whitespace) so it is not fooled by the token appearing
    /// inside prose, and is case-insensitive on the key per git trailer semantics.
    /// </summary>
    private static bool CommitBodyHasSealTrailer(string body)
    {
        if (string.IsNullOrEmpty(body))
            return false;
        foreach (var raw in body.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimStart();
            if (line.StartsWith(SealTrailerKey, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
