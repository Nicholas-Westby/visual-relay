using System.Text;

namespace VisualRelay.Core.Authorship;

public sealed partial class AuthorshipClaimer
{
    // ASCII field / record separators — safe because git format output never
    // contains them, so a multi-line %B message parses unambiguously.
    private const char FieldSep = '';
    private const char RecordSep = '';

    private readonly record struct Identity(string? Name, string? Email, ClaimOutcome? Outcome);

    private readonly record struct CommitList(IReadOnlyList<CommitInfo>? Commits, ClaimOutcome? Outcome);

    // ── Identity ───────────────────────────────────────────────────────

    private async Task<Identity> ResolveIdentityAsync(
        string repoRoot, string? claimEmail, string? claimName, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(claimEmail))
        {
            if (!claimEmail.Contains('@', StringComparison.Ordinal))
                return new Identity(null, null,
                    ClaimOutcome.Usage($"CLAIM_EMAIL must contain '@' (got: {claimEmail})"));

            var name = !string.IsNullOrEmpty(claimName)
                ? claimName
                : claimEmail[..claimEmail.IndexOf('@', StringComparison.Ordinal)];
            return new Identity(name, claimEmail, null);
        }

        // `git var GIT_AUTHOR_IDENT` prints "Name <email> ts tz".
        var (exit, output) = await RunGitAsync(repoRoot, ["var", "GIT_AUTHOR_IDENT"], ct);
        if (exit != 0)
            return new Identity(null, null,
                ClaimOutcome.Fail($"could not resolve git identity: {output.Trim()}"));

        var ident = output.Trim();
        var lt = ident.LastIndexOf('<');
        var gt = ident.LastIndexOf('>');
        if (lt < 0 || gt < lt)
            return new Identity(null, null,
                ClaimOutcome.Fail($"unparseable GIT_AUTHOR_IDENT: {ident}"));

        var email = ident.Substring(lt + 1, gt - lt - 1);
        var resolvedName = ident[..lt].TrimEnd();
        return new Identity(resolvedName, email, null);
    }

    // ── Pre-flight ─────────────────────────────────────────────────────

    private async Task<ClaimOutcome?> EnsureCleanTreeAsync(string repoRoot, CancellationToken ct)
    {
        var (exit, output) = await RunGitAsync(repoRoot, ["status", "--porcelain"], ct);
        if (exit != 0)
            return ClaimOutcome.Fail($"git status failed: {output.Trim()}");
        if (output.Trim().Length != 0)
            return ClaimOutcome.Fail(
                "working tree is not clean; commit or stash changes before claiming authorship");
        return null;
    }

    private async Task<string?> ResolveUpstreamAsync(string repoRoot, int claimCount, CancellationToken ct)
    {
        var target = $"HEAD~{claimCount}";
        var (exit, _) = await RunGitAsync(repoRoot, ["rev-parse", "--verify", "--quiet", target], ct);
        return exit == 0 ? target : null; // null == root (whole branch).
    }

    // ── Commit listing ─────────────────────────────────────────────────

    private async Task<CommitList> ListCommitsAsync(string repoRoot, string? upstream, CancellationToken ct)
    {
        var format = string.Join(FieldSep,
            "%H", "%T", "%P", "%an", "%ae", "%aI", "%ce", "%B") + RecordSep;
        var args = new List<string>
        {
            "log", "--reverse", $"--format={format}",
            upstream is null ? "HEAD" : $"{upstream}..HEAD",
        };

        var (exit, output) = await RunGitAsync(repoRoot, args, ct);
        if (exit != 0)
            return new CommitList(null, ClaimOutcome.Fail($"git log failed: {output.Trim()}"));

        var commits = ParseCommits(output, out var hasMerge);
        if (hasMerge)
            return new CommitList(null, ClaimOutcome.Fail(
                "range contains a merge commit; claiming authorship over merges is out of scope"));
        return new CommitList(commits, null);
    }

    private static List<CommitInfo> ParseCommits(string output, out bool hasMerge)
    {
        hasMerge = false;
        var commits = new List<CommitInfo>();
        foreach (var record in output.Split(RecordSep))
        {
            // The first field of the first record may carry a leading newline
            // from the previous record's trailing newline.
            var trimmed = record.TrimStart('\n');
            if (trimmed.Length == 0)
                continue;

            var f = trimmed.Split(FieldSep);
            if (f.Length < 8)
                continue;

            var parents = f[2].Length == 0
                ? Array.Empty<string>()
                : f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length > 1)
                hasMerge = true;

            // %B carries the full message including its trailing newline.
            commits.Add(new CommitInfo(
                Sha: f[0], Tree: f[1], Parents: parents,
                AuthorName: f[3], AuthorEmail: f[4], AuthorDate: f[5],
                CommitterEmail: f[6], Message: f[7]));
        }

        return commits;
    }

    // ── Replay ─────────────────────────────────────────────────────────

    private async Task<ClaimOutcome> ReplayAsync(
        string repoRoot, IReadOnlyList<CommitInfo> range,
        int firstChanged, string claimName, string claimEmail, CancellationToken ct)
    {
        // Everything before firstChanged stays sha-stable. The base parent for
        // the first rebuilt commit is:
        //   - the prior in-range commit's (unchanged) original sha, when one
        //     exists within the range, OR
        //   - that commit's original parent sha (the stable base just below the
        //     range — e.g. HEAD~N), OR
        //   - null for a root commit (firstChanged == 0 over the whole branch).
        var first = range[firstChanged];
        var newParent = firstChanged > 0
            ? range[firstChanged - 1].Sha
            : first.Parents.Count > 0 ? first.Parents[0] : null;

        var rewritten = 0;
        for (var i = firstChanged; i < range.Count; i++)
        {
            var commit = range[i];
            newParent = await CommitTreeAsync(repoRoot, commit, newParent, claimName, claimEmail, ct);
            rewritten++;
        }

        var moved = await MoveBranchAsync(repoRoot, newParent!, ct);
        if (moved is { } err)
            return err;

        return ClaimOutcome.Ok(rewrote: true, rewrittenCount: rewritten);
    }

    private async Task<string> CommitTreeAsync(
        string repoRoot, CommitInfo commit, string? parent,
        string claimName, string claimEmail, CancellationToken ct)
    {
        var cleaned = ClaudeTrailerStripper.Strip(commit.Message);
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"claim-msg-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, cleaned, new UTF8Encoding(false), ct);
        try
        {
            // Preserve the author identity when it already equals the claim;
            // otherwise re-stamp to the claim. Author date is always preserved.
            var authorMatches = string.Equals(commit.AuthorEmail, claimEmail, StringComparison.Ordinal);
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_AUTHOR_NAME"] = authorMatches ? commit.AuthorName : claimName,
                ["GIT_AUTHOR_EMAIL"] = authorMatches ? commit.AuthorEmail : claimEmail,
                ["GIT_AUTHOR_DATE"] = commit.AuthorDate,
                ["GIT_COMMITTER_NAME"] = claimName,
                ["GIT_COMMITTER_EMAIL"] = claimEmail,
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

    private async Task<ClaimOutcome?> MoveBranchAsync(string repoRoot, string newTip, CancellationToken ct)
    {
        var (oldExit, oldTip) = await RunGitAsync(repoRoot, ["rev-parse", "HEAD"], ct);
        if (oldExit != 0)
            return ClaimOutcome.Fail($"could not resolve HEAD: {oldTip.Trim()}");

        var (symExit, branch) = await RunGitAsync(repoRoot, ["symbolic-ref", "--short", "--quiet", "HEAD"], ct);
        var refName = symExit == 0 ? $"refs/heads/{branch.Trim()}" : "HEAD";

        var (exit, output) = await RunGitAsync(repoRoot,
            ["update-ref", refName, newTip, oldTip.Trim()], ct);
        if (exit != 0)
            return ClaimOutcome.Fail($"git update-ref failed: {output.Trim()}");
        return null;
    }
}
