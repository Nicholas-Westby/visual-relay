using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary><c>read-tree &lt;tree-ish&gt;</c>: replaces the (possibly <c>GIT_INDEX_FILE</c>) index with the tree's contents.</summary>
    public static GitSimResult ReadTree(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));
        var treeIsh = operand is null ? null : ResolveRevision(wt, operand) ?? operand;
        if (treeIsh is null || TreeBuilder.ResolveTreeSha(wt.Repo.Objects, treeIsh) is null)
            return GitSimResult.Fatal($"not a valid object name {operand}");

        TreeBuilder.ReadTreeIntoIndex(wt.Repo.Objects, ctx.Index(wt), treeIsh);
        return GitSimResult.Ok();
    }

    /// <summary><c>write-tree</c>: writes the index as a tree and prints the root tree sha.</summary>
    public static GitSimResult WriteTree(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");
        var sha = TreeBuilder.BuildTreeFromIndex(wt.Repo.Objects, ctx.Index(wt));
        return GitSimResult.Ok(sha + "\n");
    }

    /// <summary>
    /// <c>update-ref</c>: set (<c>&lt;ref&gt; &lt;new&gt;</c>), compare-and-swap
    /// (<c>&lt;ref&gt; &lt;new&gt; &lt;old&gt;</c> — fails when the current value differs),
    /// or delete (<c>-d &lt;ref&gt;</c>).
    /// </summary>
    public static GitSimResult UpdateRef(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var refs = wt.Repo.Refs;
        if (ctx.Has("-d"))
        {
            var refName = ctx.ValueAfter("-d");
            if (refName is not null)
                refs.Remove(refName);
            return GitSimResult.Ok();
        }

        var positional = ctx.Args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (positional.Count < 2)
            return ctx.Unsupported();

        var name = positional[0];
        var newSha = ResolveRevision(wt, positional[1]) ?? positional[1];
        if (positional.Count >= 3)
        {
            var expected = ResolveRevision(wt, positional[2]) ?? positional[2];
            var current = refs.GetValueOrDefault(name);
            if (!string.Equals(current, expected, StringComparison.Ordinal))
                return GitSimResult.Code(1,
                    $"fatal: update_ref failed for ref '{name}': cannot lock ref: expected {expected}\n");
        }

        refs[name] = newSha;
        return GitSimResult.Ok();
    }

    /// <summary>
    /// <c>rm --cached [-q] [-f] [--ignore-unmatch] -- &lt;path&gt;</c>: drops matching
    /// entries from the index while leaving the working tree in place.
    /// </summary>
    public static GitSimResult Rm(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");
        if (!ctx.Has("--cached"))
            return ctx.Unsupported();

        var index = ctx.Index(wt);
        var pathspecs = ctx.Pathspecs();
        var matched = index.Stage0.Select(e => e.Path).Where(p => MatchesAny(p, pathspecs)).ToList();
        if (matched.Count == 0 && !ctx.Has("--ignore-unmatch"))
            return GitSimResult.Fatal($"pathspec '{pathspecs.FirstOrDefault()}' did not match any files");

        foreach (var path in matched)
            index.Remove(path);
        return GitSimResult.Ok();
    }
}
