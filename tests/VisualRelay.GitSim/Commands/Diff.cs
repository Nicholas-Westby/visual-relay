namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>diff</c> across the HEAD / index / working-tree / two-rev axes with
    /// <c>--cached</c>, <c>--name-only</c>, <c>--name-status</c>, <c>--diff-filter</c>,
    /// <c>--quiet</c>, <c>-z</c>, and a trailing pathspec. Rename/copy detection
    /// (<c>-M</c>/<c>-C</c>) is accepted but not performed — renames read as an add
    /// plus a delete, which the suite's consumers tolerate.
    /// </summary>
    public static GitSimResult Diff(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);
        var cached = ctx.Has("--cached");
        var nul = ctx.Has("-z");
        var pathspecs = ctx.Pathspecs();

        var revs = new List<string>();
        foreach (var a in ctx.Args.Skip(1))
        {
            if (a == "--")
                break;
            if (!a.StartsWith('-'))
                revs.Add(a);
        }

        Snapshot from, to;
        if (revs.Count == 2)
        {
            from = TreeSnapshot(store, ResolveRevision(wt, revs[0]));
            to = TreeSnapshot(store, ResolveRevision(wt, revs[1]));
        }
        else if (revs.Count == 1)
        {
            from = TreeSnapshot(store, ResolveRevision(wt, revs[0]));
            to = cached
                ? IndexSnapshot(index)
                : WorktreeSnapshot(wt, from.Paths.Concat(index.Stage0.Select(e => e.Path)));
        }
        else
        {
            from = cached ? HeadSnapshot(wt) : IndexSnapshot(index);
            to = cached ? IndexSnapshot(index) : WorktreeSnapshot(wt, index.Stage0.Select(e => e.Path));
        }

        var changes = FilterByPathspec(ApplyDiffFilter(ctx, Diff(from, to)), pathspecs);

        if (ctx.Has("--quiet"))
            return GitSimResult.Code(changes.Count > 0 ? 1 : 0);

        return GitSimResult.Ok(FormatChanges(changes, ctx.Has("--name-status"), nul));
    }

    /// <summary>
    /// <c>diff-tree --no-commit-id --name-only -r &lt;sha&gt;</c>: the paths a commit
    /// changed relative to its first parent (all paths added, for a root commit).
    /// </summary>
    public static GitSimResult DiffTree(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));
        var sha = operand is null ? null : ResolveRevision(wt, operand);
        if (sha is null || !store.TryGetCommit(sha, out var commit))
            return GitSimResult.Fatal($"not a tree object: {operand}");

        var parentTree = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var changes = ApplyDiffFilter(ctx, Diff(TreeSnapshot(store, parentTree), TreeSnapshot(store, sha)));
        return GitSimResult.Ok(FormatChanges(changes, ctx.Has("--name-status"), ctx.Has("-z")));
    }

    private static IReadOnlyList<(DiffStatus Status, string Path)> ApplyDiffFilter(
        GitSimContext ctx, IReadOnlyList<(DiffStatus Status, string Path)> changes)
    {
        var spec = ctx.Args.FirstOrDefault(a => a.StartsWith("--diff-filter=", StringComparison.Ordinal));
        if (spec is null)
            return changes;
        var letters = spec["--diff-filter=".Length..].ToUpperInvariant();
        return changes.Where(c => letters.Contains(StatusLetter(c.Status))).ToList();
    }

    private static string FormatChanges(
        IReadOnlyList<(DiffStatus Status, string Path)> changes, bool nameStatus, bool nul)
    {
        if (!nameStatus)
            return JoinPaths(changes.Select(c => c.Path), nul);

        if (changes.Count == 0)
            return string.Empty;
        return nul
            ? string.Concat(changes.Select(c => $"{StatusLetter(c.Status)}\0{c.Path}\0"))
            : string.Join('\n', changes.Select(c => $"{StatusLetter(c.Status)}\t{c.Path}")) + "\n";
    }
}
