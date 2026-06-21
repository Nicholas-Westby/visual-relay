using VisualRelay.Core.Execution;

namespace VisualRelay.Core.CommitLint;

/// <summary>
/// Self-contained in-process history-rewrite engine: export every commit in a
/// range (oldest→newest), let the caller supply a conforming rewrite per commit,
/// validate them all, then rebuild from root via <c>git commit-tree</c> and move
/// the branch ref last. The new tip's tree equals the old tip's tree, so the
/// working tree and index are untouched. Idempotent, linear-history only, and
/// it routes every git call through <see cref="IGitInvoker"/> — it does not
/// reuse the authorship-claim engine.
/// </summary>
public sealed partial class HistoryRewriter(IGitInvoker git)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // ASCII field / record separators — git format output never contains them,
    // so a multi-line %B message parses unambiguously.
    private const char FieldSep = '';
    private const char RecordSep = '';

    /// <summary>The backup tag created at the old tip before the branch moves.</summary>
    public const string BackupTag = "backup/pre-conform";

    /// <summary>
    /// Exports the commits in <paramref name="range"/> oldest→newest (default
    /// the whole branch). Fails fast if the range contains a merge commit (the
    /// repo is linear). Each record carries the per-commit changed-file
    /// basenames so the replay can validate against the full ruleset.
    /// </summary>
    public async Task<RewriteExport> ExportAsync(string repoRoot, string? range, CancellationToken ct)
    {
        var format = string.Join(FieldSep,
            "%H", "%T", "%P", "%an", "%ae", "%aI", "%cn", "%ce", "%cI", "%B") + RecordSep;
        var revRange = string.IsNullOrWhiteSpace(range) ? "HEAD" : range;
        var (exit, output) = await RunGitAsync(repoRoot, ["log", "--reverse", $"--format={format}", revRange], ct);
        if (exit != 0)
            return RewriteExport.Fail($"git log failed: {output.Trim()}");

        var commits = ParseCommits(output, out var hasMerge);
        if (hasMerge)
            return RewriteExport.Fail("merge commits out of scope: the range contains a merge commit");
        if (commits.Count == 0)
            return RewriteExport.Fail("no commits in range");

        for (var i = 0; i < commits.Count; i++)
        {
            var changed = await ChangedBasenamesAsync(repoRoot, commits[i].Sha, ct);
            commits[i] = commits[i] with { ChangedBasenames = changed };
        }

        var (branchRef, oldTip, refErr) = await ResolveBranchAsync(repoRoot, ct);
        if (refErr is not null)
            return RewriteExport.Fail(refErr);

        return RewriteExport.Ok(commits, branchRef!, oldTip!);
    }

    private List<RewriteCommit> ParseCommits(string output, out bool hasMerge)
    {
        hasMerge = false;
        var commits = new List<RewriteCommit>();
        foreach (var record in output.Split(RecordSep))
        {
            var trimmed = record.TrimStart('\n');
            if (trimmed.Length == 0)
                continue;

            var f = trimmed.Split(FieldSep);
            if (f.Length < 10)
                continue;

            var parents = f[2].Length == 0
                ? Array.Empty<string>()
                : f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length > 1)
                hasMerge = true;

            commits.Add(new RewriteCommit(
                Sha: f[0], Tree: f[1], Parents: parents,
                AuthorName: f[3], AuthorEmail: f[4], AuthorDate: f[5],
                CommitterName: f[6], CommitterEmail: f[7], CommitterDate: f[8],
                Message: f[9], ChangedBasenames: []));
        }

        return commits;
    }

    private async Task<IReadOnlyList<string>> ChangedBasenamesAsync(
        string repoRoot, string sha, CancellationToken ct)
    {
        var (exit, output) = await RunGitAsync(
            repoRoot, ["diff-tree", "--no-commit-id", "--name-only", "-r", sha], ct);
        if (exit != 0)
            return [];

        var names = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileName(line.Trim());
            if (name.Length > 0)
                names.Add(name);
        }

        return names;
    }

    private async Task<(string? BranchRef, string? OldTip, string? Error)> ResolveBranchAsync(
        string repoRoot, CancellationToken ct)
    {
        var (headExit, head) = await RunGitAsync(repoRoot, ["rev-parse", "HEAD"], ct);
        if (headExit != 0)
            return (null, null, $"could not resolve HEAD: {head.Trim()}");

        var (symExit, branch) = await RunGitAsync(repoRoot, ["symbolic-ref", "--short", "--quiet", "HEAD"], ct);
        var refName = symExit == 0 ? $"refs/heads/{branch.Trim()}" : "HEAD";
        return (refName, head.Trim(), null);
    }

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
