namespace VisualRelay.Core.Execution;

internal static partial class GitCommitter
{
    /// <summary>
    /// Soft-resets HEAD back to <paramref name="runBaseSha"/> so any commits the
    /// agent made during the run are un-committed but their changes stay staged,
    /// to be folded into the single sealed commit. Returns <c>null</c> when there
    /// is nothing to do (no run-base supplied, run-base is HEAD, or run-base is
    /// not an ancestor of HEAD — leave history untouched and let the caller
    /// proceed) or a failure <see cref="GitCommitResult"/> when the reset itself
    /// fails.
    /// </summary>
    private static async Task<GitCommitResult?> SquashInRunCommitsAsync(
        IGitInvoker gi,
        string rootPath,
        string? runBaseSha,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runBaseSha))
            return null;

        // Resolve the run-base to a full sha. If it can't be resolved (the
        // recorded sha is gone, e.g. an unrelated history rewrite), do nothing
        // rather than risk a destructive reset to a bad ref.
        var baseRev = await GitAsync(gi, rootPath, ["rev-parse", "--verify", "--quiet", $"{runBaseSha}^{{commit}}"], cancellationToken);
        if (baseRev.ExitCode != 0 || string.IsNullOrWhiteSpace(baseRev.Output))
            return null;
        var resolvedBase = baseRev.Output.Trim();

        var headRev = await GitAsync(gi, rootPath, ["rev-parse", "HEAD"], cancellationToken);
        if (headRev.ExitCode != 0)
            return null;
        var head = headRev.Output.Trim();

        // HEAD already at run-base → no in-run commits to squash. This is the
        // normal path when the agent did not self-commit; behaviour unchanged.
        if (string.Equals(head, resolvedBase, StringComparison.Ordinal))
            return null;

        // Only squash when the run-base is a strict ancestor of HEAD. If it is
        // not (detached/diverged HEAD, or a bad recorded sha), resetting would
        // discard or rewrite unrelated history — skip and let the working-tree
        // commit proceed against the current HEAD.
        var ancestor = await GitAsync(gi, rootPath, ["merge-base", "--is-ancestor", resolvedBase, head], cancellationToken);
        if (ancestor.ExitCode != 0)
            return null;

        // Soft reset: keeps every change since the run-base staged, drops the
        // agent's bare commits, repoints HEAD at the run-base.
        var reset = await GitAsync(gi, rootPath, ["reset", "--soft", resolvedBase], cancellationToken);
        if (reset.ExitCode != 0)
            return GitCommitResult.Failed($"git reset --soft to run-base failed (git exit {reset.ExitCode}): {reset.Output.Trim()}");

        return null;
    }
}
