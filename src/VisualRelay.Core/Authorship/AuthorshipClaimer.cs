using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Authorship;

/// <summary>
/// Claims author and committer on the last N commits of the current branch so
/// both fields become a single chosen identity (preserving author dates), and
/// strips any commit-message trailer mentioning "Claude". The C# port of the
/// former <c>me.sh</c>: an in-process commit-tree replay through
/// <see cref="IGitInvoker"/> — no <c>git rebase --exec</c> self-callback.
/// </summary>
/// <remarks>
/// Behavior contract preserved from <c>me.sh</c>: default 5 / <c>-N</c>;
/// <c>--root</c> fallback when <c>HEAD~N</c> does not resolve;
/// <c>CLAIM_EMAIL</c>/<c>CLAIM_NAME</c> override; author-date preservation;
/// empty commits tolerated (<c>commit-tree</c> needs no <c>--allow-empty</c>);
/// hooks bypassed (<c>commit-tree</c> runs none); idempotent re-runs.
/// </remarks>
public sealed partial class AuthorshipClaimer(IGitInvoker git)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Rewrites up to <paramref name="claimCount"/> commits back from HEAD.
    /// </summary>
    /// <param name="repoRoot">Repository working-tree root.</param>
    /// <param name="claimCount">Commits back from HEAD to consider (e.g. 5).</param>
    /// <param name="claimEmail"><c>CLAIM_EMAIL</c> override, or null to read the
    /// repo's configured identity via <c>git var GIT_AUTHOR_IDENT</c>.</param>
    /// <param name="claimName"><c>CLAIM_NAME</c> override; defaults to the
    /// local-part of <paramref name="claimEmail"/> when only the email is set.</param>
    /// <param name="ct">Cancellation token for the underlying git calls.</param>
    public async Task<ClaimOutcome> ClaimAsync(
        string repoRoot, int claimCount, string? claimEmail, string? claimName,
        CancellationToken ct)
    {
        var identity = await ResolveIdentityAsync(repoRoot, claimEmail, claimName, ct);
        if (identity.Outcome is { } usage)
            return usage;
        var (name, email) = (identity.Name!, identity.Email!);

        var clean = await EnsureCleanTreeAsync(repoRoot, ct);
        if (clean is { } dirty)
            return dirty;

        var upstream = await ResolveUpstreamAsync(repoRoot, claimCount, ct);
        var commits = await ListCommitsAsync(repoRoot, upstream, ct);
        if (commits.Outcome is { } listErr)
            return listErr;
        var range = commits.Commits!;
        if (range.Count == 0)
            return ClaimOutcome.Ok(rewrote: false, rewrittenCount: 0);

        var firstChanged = FindFirstNeedingChange(range, email);
        if (firstChanged < 0)
            return ClaimOutcome.Ok(rewrote: false, rewrittenCount: 0); // idempotent no-op.

        return await ReplayAsync(repoRoot, range, firstChanged, name, email, ct);
    }

    /// <summary>
    /// A commit needs change iff its author email != claim, OR committer email
    /// != claim, OR stripping Claude trailers alters its message.
    /// </summary>
    private static int FindFirstNeedingChange(IReadOnlyList<CommitInfo> range, string claimEmail)
    {
        for (var i = 0; i < range.Count; i++)
        {
            if (NeedsChange(range[i], claimEmail))
                return i;
        }

        return -1;
    }

    private static bool NeedsChange(CommitInfo commit, string claimEmail) =>
        !string.Equals(commit.AuthorEmail, claimEmail, StringComparison.Ordinal)
        || !string.Equals(commit.CommitterEmail, claimEmail, StringComparison.Ordinal)
        || !string.Equals(ClaudeTrailerStripper.Strip(commit.Message), commit.Message, StringComparison.Ordinal);

    private async Task<(int ExitCode, string Output)> RunGitAsync(
        string repoRoot, IReadOnlyList<string> args, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var (exit, output, timedOut) = await git.RunAsync(repoRoot, args, ct, Timeout, env);
        if (timedOut)
            throw new TimeoutException($"git {string.Join(' ', args)} timed out");
        return (exit, output);
    }
}
