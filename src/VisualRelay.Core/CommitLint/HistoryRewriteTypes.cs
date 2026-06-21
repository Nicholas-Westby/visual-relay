namespace VisualRelay.Core.CommitLint;

/// <summary>
/// One exported commit: its sha, tree, parents, author/committer identity and
/// dates, and the raw message. Oldest first. The implementing agent reads these
/// to produce a conforming rewrite per <see cref="Sha"/>.
/// </summary>
public sealed record RewriteCommit(
    string Sha,
    string Tree,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    string AuthorDate,
    string CommitterName,
    string CommitterEmail,
    string CommitterDate,
    string Message,
    IReadOnlyList<string> ChangedBasenames);

/// <summary>
/// Result of <see cref="HistoryRewriter.ExportAsync"/>: the ordered commit
/// records (oldest first) plus the branch ref and old tip, or a failure.
/// </summary>
public sealed record RewriteExport(
    bool Success,
    IReadOnlyList<RewriteCommit>? Commits,
    string? BranchRef,
    string? OldTip,
    string? Error)
{
    public static RewriteExport Ok(
        IReadOnlyList<RewriteCommit> commits, string branchRef, string oldTip) =>
        new(true, commits, branchRef, oldTip, Error: null);

    public static RewriteExport Fail(string error) =>
        new(false, Commits: null, BranchRef: null, OldTip: null, error);
}

/// <summary>
/// Result of <see cref="HistoryRewriter.ReplayAsync"/>.
/// </summary>
/// <param name="Success">True when the replay completed (including a no-op).</param>
/// <param name="Rewrote">True when commits were rebuilt and the ref moved.</param>
/// <param name="RewrittenCount">Number of commits rebuilt (0 on a no-op).</param>
/// <param name="Error">Failure message, or <c>null</c> on success.</param>
public sealed record RewriteOutcome(
    bool Success,
    bool Rewrote,
    int RewrittenCount,
    string? Error)
{
    public static RewriteOutcome Ok(bool rewrote, int rewrittenCount) =>
        new(true, rewrote, rewrittenCount, Error: null);

    public static RewriteOutcome NoOp() => new(true, Rewrote: false, RewrittenCount: 0, Error: null);

    public static RewriteOutcome Fail(string error) =>
        new(false, Rewrote: false, RewrittenCount: 0, error);
}
