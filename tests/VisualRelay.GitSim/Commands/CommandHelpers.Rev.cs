using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

/// <summary>Revision resolution, ancestry and range enumeration shared across handlers.</summary>
internal static partial class GitSimCommands
{
    /// <summary>
    /// Resolves a revision string to a commit sha, or null when it does not resolve.
    /// Handles <c>HEAD</c>, <c>HEAD~N</c>, <c>&lt;rev&gt;^{commit}</c>, full/abbreviated
    /// commit shas, full ref names, and bare branch/tag names.
    /// </summary>
    public static string? ResolveRevision(Worktree wt, string rev)
    {
        var store = wt.Repo.Objects;

        if (rev == "HEAD")
            return wt.ResolveHead();

        if (rev.StartsWith("HEAD~", StringComparison.Ordinal)
            && int.TryParse(rev.AsSpan(5), out var n))
            return WalkFirstParent(store, wt.ResolveHead(), n);

        if (rev.EndsWith("^{commit}", StringComparison.Ordinal))
        {
            var inner = ResolveRevision(wt, rev[..^"^{commit}".Length]);
            return inner is not null && store.IsCommit(inner) ? inner : null;
        }

        if (wt.Repo.Refs.TryGetValue(rev, out var direct))
            return direct;
        if (wt.Repo.Refs.TryGetValue($"refs/heads/{rev}", out var branch))
            return branch;
        if (wt.Repo.Refs.TryGetValue($"refs/tags/{rev}", out var tag))
            return tag;

        if (store.IsCommit(rev))
            return rev;

        // Abbreviated sha (>= 4 hex) that uniquely prefixes a known commit.
        if (rev.Length is >= 4 and < 40 && IsHex(rev))
        {
            var matches = AllCommitShas(wt).Where(s => s.StartsWith(rev, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
                return matches[0];
        }

        return null;
    }

    private static string? WalkFirstParent(GitObjectStore store, string? sha, int steps)
    {
        for (var i = 0; i < steps && sha is not null; i++)
            sha = store.TryGetCommit(sha, out var commit) && commit.Parents.Count > 0 ? commit.Parents[0] : null;
        return sha;
    }

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="descendant"/> or reachable from it via parents.</summary>
    public static bool IsAncestor(GitObjectStore store, string ancestor, string descendant)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(descendant);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == ancestor)
                return true;
            if (!seen.Add(cur) || !store.TryGetCommit(cur, out var commit))
                continue;
            foreach (var p in commit.Parents)
                stack.Push(p);
        }

        return false;
    }

    /// <summary>All commit shas reachable from <paramref name="start"/> (inclusive).</summary>
    public static HashSet<string> ReachableFrom(GitObjectStore store, string? start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (start is null)
            return seen;
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur) || !store.TryGetCommit(cur, out var commit))
                continue;
            foreach (var p in commit.Parents)
                stack.Push(p);
        }

        return seen;
    }

    /// <summary>Commits reachable from <paramref name="tip"/> but not from <paramref name="baseRev"/>, newest first.</summary>
    public static IReadOnlyList<string> RangeCommits(Worktree wt, string baseRev, string tip)
    {
        var store = wt.Repo.Objects;
        var excluded = ReachableFrom(store, baseRev);
        var included = ReachableFrom(store, tip).Where(s => !excluded.Contains(s));
        return OrderByDateDescending(store, included);
    }

    /// <summary>Orders commits newest-committer-date first (stable for linear history).</summary>
    public static IReadOnlyList<string> OrderByDateDescending(GitObjectStore store, IEnumerable<string> shas) =>
        shas.Where(store.IsCommit)
            .OrderByDescending(s => store.TryGetCommit(s, out var c) ? c.Committer.When : DateTimeOffset.MinValue)
            .ThenByDescending(s => s, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> AllCommitShas(Worktree wt) =>
        ReachableFrom(wt.Repo.Objects, wt.ResolveHead())
            .Concat(wt.Repo.Refs.Values)
            .Distinct(StringComparer.Ordinal);

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);
}
