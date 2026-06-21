using System.Text;

namespace VisualRelay.Core.CommitLint;

public sealed partial class HistoryRewriter
{
    /// <summary>
    /// Validates every supplied rewrite against the full ruleset, then (unless
    /// it is an idempotent no-op) requires a clean working tree, tags the old
    /// tip, rebuilds each commit from root via <c>git commit-tree</c> preserving
    /// author identity + author date, and moves the branch ref last.
    /// </summary>
    /// <param name="repoRoot">Repository working-tree root.</param>
    /// <param name="export">The prior <see cref="ExportAsync"/> result.</param>
    /// <param name="rewrites">New message per original commit sha. Every commit
    /// in <paramref name="export"/> must have an entry.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RewriteOutcome> ReplayAsync(
        string repoRoot, RewriteExport export,
        IReadOnlyDictionary<string, string> rewrites, CancellationToken ct)
    {
        if (!export.Success || export.Commits is null)
            return RewriteOutcome.Fail(export.Error ?? "export failed");
        var commits = export.Commits;

        var prepared = PrepareRewrites(commits, rewrites);
        if (prepared.Error is not null)
            return RewriteOutcome.Fail(prepared.Error);
        var newMessages = prepared.Messages!;

        // Idempotent: if every commit already validates and no message changes,
        // do nothing (no ref move, no backup tag).
        if (IsNoOp(commits, newMessages))
            return RewriteOutcome.NoOp();

        var clean = await EnsureCleanTreeAsync(repoRoot, ct);
        if (clean is not null)
            return RewriteOutcome.Fail(clean);

        var tagged = await CreateBackupTagAsync(repoRoot, export.OldTip!, ct);
        if (tagged is not null)
            return RewriteOutcome.Fail(tagged);

        return await RebuildAsync(repoRoot, export, newMessages, ct);
    }

    private static (IReadOnlyList<string>? Messages, string? Error) PrepareRewrites(
        IReadOnlyList<RewriteCommit> commits, IReadOnlyDictionary<string, string> rewrites)
    {
        var messages = new List<string>(commits.Count);
        foreach (var commit in commits)
        {
            if (!rewrites.TryGetValue(commit.Sha, out var message))
                return (null, $"missing rewrite for commit {commit.Sha[..Math.Min(8, commit.Sha.Length)]}");

            var ctx = CommitLintContext.Human(commit.ChangedBasenames, []);
            var violations = CommitMessageValidator.Validate(message, ctx);
            if (violations.Count > 0)
            {
                var detail = string.Join("; ", violations.Select(v => v.Message));
                return (null, $"rewritten message for {commit.Sha[..Math.Min(8, commit.Sha.Length)]} still invalid: {detail}");
            }

            messages.Add(message);
        }

        return (messages, null);
    }

    private static bool IsNoOp(IReadOnlyList<RewriteCommit> commits, IReadOnlyList<string> newMessages)
    {
        for (var i = 0; i < commits.Count; i++)
        {
            // Compare ignoring a single trailing newline difference, since %B
            // carries one and a hand-written rewrite usually does not.
            if (!string.Equals(commits[i].Message.TrimEnd('\n'), newMessages[i].TrimEnd('\n'), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private async Task<string?> EnsureCleanTreeAsync(string repoRoot, CancellationToken ct)
    {
        var (exit, output) = await RunGitAsync(repoRoot, ["status", "--porcelain"], ct);
        if (exit != 0)
            return $"git status failed: {output.Trim()}";
        return output.Trim().Length != 0
            ? "working tree is not clean; commit or stash changes before rewriting history"
            : null;
    }

    private async Task<string?> CreateBackupTagAsync(string repoRoot, string oldTip, CancellationToken ct)
    {
        // -f so a re-run after a prior aborted rewrite refreshes the backup.
        var (exit, output) = await RunGitAsync(repoRoot, ["tag", "-f", BackupTag, oldTip], ct);
        return exit != 0 ? $"could not create backup tag: {output.Trim()}" : null;
    }

    private async Task<RewriteOutcome> RebuildAsync(
        string repoRoot, RewriteExport export,
        IReadOnlyList<string> newMessages, CancellationToken ct)
    {
        var commits = export.Commits!;
        var first = commits[0];
        string? newParent = first.Parents.Count > 0 ? first.Parents[0] : null;

        for (var i = 0; i < commits.Count; i++)
        {
            newParent = await CommitTreeAsync(repoRoot, commits[i], newMessages[i], newParent, ct);
        }

        var moved = await MoveBranchAsync(repoRoot, export.BranchRef!, export.OldTip!, newParent!, ct);
        if (moved is not null)
            return RewriteOutcome.Fail(moved);

        return RewriteOutcome.Ok(rewrote: true, rewrittenCount: commits.Count);
    }

    private async Task<string> CommitTreeAsync(
        string repoRoot, RewriteCommit commit, string message, string? parent, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"conform-msg-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, message, new UTF8Encoding(false), ct);
        try
        {
            // Preserve author identity + author date; committer per policy: keep
            // the original committer identity but stamp the committer date to the
            // original author date so the rewrite is reproducible.
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_AUTHOR_NAME"] = commit.AuthorName,
                ["GIT_AUTHOR_EMAIL"] = commit.AuthorEmail,
                ["GIT_AUTHOR_DATE"] = commit.AuthorDate,
                ["GIT_COMMITTER_NAME"] = commit.CommitterName,
                ["GIT_COMMITTER_EMAIL"] = commit.CommitterEmail,
                ["GIT_COMMITTER_DATE"] = commit.CommitterDate,
            };

            var args = new List<string> { "commit-tree", commit.Tree };
            if (parent is not null)
            {
                args.Add("-p");
                args.Add(parent);
            }

            args.Add("-F");
            args.Add(tempFile);

            var (exit, output) = await RunGitAsync(repoRoot, args, ct, env);
            if (exit != 0)
                throw new InvalidOperationException($"git commit-tree failed: {output.Trim()}");
            return output.Trim();
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort */ }
        }
    }

    private async Task<string?> MoveBranchAsync(
        string repoRoot, string refName, string oldTip, string newTip, CancellationToken ct)
    {
        var (exit, output) = await RunGitAsync(repoRoot, ["update-ref", refName, newTip, oldTip], ct);
        return exit != 0 ? $"git update-ref failed: {output.Trim()}" : null;
    }
}
