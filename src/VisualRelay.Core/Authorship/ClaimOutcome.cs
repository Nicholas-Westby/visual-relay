namespace VisualRelay.Core.Authorship;

/// <summary>
/// Result of an <see cref="AuthorshipClaimer.ClaimAsync"/> run.
/// </summary>
/// <param name="Success">True when the run completed (including a no-op).</param>
/// <param name="Rewrote">True when at least one commit was rebuilt and the
/// branch ref was moved; false for an idempotent no-op.</param>
/// <param name="RewrittenCount">Number of commits rebuilt (0 on a no-op).</param>
/// <param name="Error">Human-readable failure message, or <c>null</c> on success.</param>
/// <param name="IsUsageError">True when the failure is a usage error (e.g. an
/// invalid <c>CLAIM_EMAIL</c>), which the CLI surfaces as exit code 64.</param>
public sealed record ClaimOutcome(
    bool Success,
    bool Rewrote,
    int RewrittenCount,
    string? Error,
    bool IsUsageError)
{
    public static ClaimOutcome Ok(bool rewrote, int rewrittenCount) =>
        new(true, rewrote, rewrittenCount, Error: null, IsUsageError: false);

    public static ClaimOutcome Fail(string error) =>
        new(false, Rewrote: false, RewrittenCount: 0, error, IsUsageError: false);

    public static ClaimOutcome Usage(string error) =>
        new(false, Rewrote: false, RewrittenCount: 0, error, IsUsageError: true);
}

/// <summary>
/// One commit in the range to (potentially) rewrite, as parsed from
/// <c>git log --reverse</c> + a per-commit format. Oldest first.
/// </summary>
internal sealed record CommitInfo(
    string Sha,
    string Tree,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    string AuthorDate,
    string CommitterEmail,
    string Message);
