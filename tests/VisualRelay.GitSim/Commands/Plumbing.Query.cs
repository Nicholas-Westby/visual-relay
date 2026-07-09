using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary><c>cat-file -e &lt;rev&gt;:&lt;path&gt;</c>: exit 0 when the path exists in the revision's tree, else fatal.</summary>
    public static GitSimResult CatFile(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");
        if (!ctx.Has("-e"))
            return ctx.Unsupported();

        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));
        var colon = operand?.IndexOf(':') ?? -1;
        if (operand is null || colon < 0)
            return ctx.Unsupported();

        var rev = ResolveRevision(wt, operand[..colon]);
        var path = operand[(colon + 1)..];
        return rev is not null && TreeBuilder.Lookup(wt.Repo.Objects, rev, path) is not null
            ? GitSimResult.Ok()
            : GitSimResult.Fatal($"path '{path}' does not exist in '{operand[..colon]}'");
    }

    /// <summary><c>merge-base --is-ancestor &lt;a&gt; &lt;b&gt;</c>: exit 0 when A is an ancestor of B, else exit 1.</summary>
    public static GitSimResult MergeBase(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");
        if (!ctx.Has("--is-ancestor"))
            return ctx.Unsupported();

        var revs = ctx.Args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (revs.Count < 2)
            return ctx.Unsupported();
        var a = ResolveRevision(wt, revs[0]);
        var b = ResolveRevision(wt, revs[1]);
        if (a is null || b is null)
            return GitSimResult.Code(1);
        return GitSimResult.Code(IsAncestor(wt.Repo.Objects, a, b) ? 0 : 1);
    }

    /// <summary><c>rev-list &lt;a&gt;..&lt;b&gt;</c> (or <c>rev-list &lt;rev&gt;</c>): commit shas newest first, one per line.</summary>
    public static GitSimResult RevList(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));
        if (operand is null)
            return ctx.Unsupported();

        IReadOnlyList<string> shas;
        var dots = operand.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var baseRev = ResolveRevision(wt, operand[..dots]);
            var tip = ResolveRevision(wt, operand[(dots + 2)..]);
            shas = baseRev is null || tip is null ? [] : RangeCommits(wt, baseRev, tip);
        }
        else
        {
            var tip = ResolveRevision(wt, operand);
            shas = tip is null ? [] : OrderByDateDescending(wt.Repo.Objects, ReachableFrom(wt.Repo.Objects, tip));
        }

        return GitSimResult.Ok(JoinPaths(shas, nul: false));
    }

    /// <summary>
    /// <c>ls-tree &lt;rev&gt; -- &lt;path&gt;</c>: emits a <c>&lt;mode&gt; blob &lt;sha&gt;\t&lt;path&gt;</c>
    /// line for each tracked path matched (so a non-empty result signals the path is
    /// tracked at that revision — the check consumers actually make).
    /// </summary>
    public static GitSimResult LsTree(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var rev = ResolveRevision(wt, ctx.Args.ElementAtOrDefault(1) ?? "HEAD");
        var pathspecs = ctx.Pathspecs();
        if (rev is null)
            return GitSimResult.Ok();

        var tree = TreeBuilder.FlattenTree(wt.Repo.Objects, TreeBuilder.ResolveTreeSha(wt.Repo.Objects, rev) ?? rev);
        var lines = tree
            .Where(kv => MatchesAny(kv.Key, pathspecs))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Value.Mode} blob {kv.Value.Sha}\t{kv.Key}");
        return GitSimResult.Ok(JoinPaths(lines, nul: false));
    }
}
