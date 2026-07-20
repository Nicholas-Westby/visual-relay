using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>reset -q [HEAD]</c> (mixed: rebuild the index from HEAD, keep the working
    /// tree) and <c>reset --soft &lt;sha&gt;</c> (move HEAD, keep index + working tree).
    /// </summary>
    public static GitSimResult Reset(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);
        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));

        if (ctx.Has("--soft"))
        {
            var soft = operand is null ? null : ResolveRevision(wt, operand);
            if (soft is null)
                return GitSimResult.Fatal($"ambiguous argument '{operand}': unknown revision");
            wt.MoveHeadTo(soft);
            return GitSimResult.Ok();
        }

        // Mixed reset. A target other than HEAD also repoints HEAD.
        var targetSha = operand is null or "HEAD" ? wt.ResolveHead() : ResolveRevision(wt, operand);
        if (operand is not null && operand != "HEAD" && targetSha is not null)
            wt.MoveHeadTo(targetSha);

        if (targetSha is null)
            index.Clear();
        else
            TreeBuilder.ReadTreeIntoIndex(store, index, targetSha);
        return GitSimResult.Ok();
    }

    /// <summary>
    /// <c>restore --staged --source=&lt;sha&gt; -- &lt;path&gt;</c> (and the
    /// <c>--source &lt;sha&gt;</c> spelling): rewrites the index entries under the
    /// pathspec to the source tree's versions — adding, replacing, or removing — with
    /// no working-tree change. The pathspec may be a directory prefix.
    /// </summary>
    public static GitSimResult Restore(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");
        if (!ctx.Has("--staged"))
            return ctx.Unsupported();

        var source = ctx.Args.FirstOrDefault(a => a.StartsWith("--source=", StringComparison.Ordinal))
            is { } inline
            ? inline["--source=".Length..]
            : ctx.ValueAfter("--source");
        if (source is null)
            return ctx.Unsupported();

        var sourceSha = ResolveRevision(wt, source);
        if (sourceSha is null)
            return GitSimResult.Fatal($"could not resolve {source}");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);
        var sourceTree = TreeBuilder.FlattenTree(store, TreeBuilder.ResolveTreeSha(store, sourceSha) ?? sourceSha);
        var pathspecs = ctx.Pathspecs();

        var affected = index.Stage0.Select(e => e.Path)
            .Concat(sourceTree.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(p => MatchesAny(p, pathspecs));

        foreach (var path in affected)
        {
            if (sourceTree.TryGetValue(path, out var entry))
                index.Set(path, entry);
            else
                index.Remove(path);
        }

        return GitSimResult.Ok();
    }

    /// <summary>
    /// <c>checkout &lt;rev&gt; -- &lt;path&gt;</c> (overwrite the working tree + index
    /// with the revision's version),
    /// <c>checkout -b &lt;branch&gt;</c> (create and switch to a new branch at HEAD),
    /// <c>checkout &lt;branch&gt;</c> (switch to an existing branch),
    /// and <c>checkout -- .</c> (restore the working tree from the index).
    /// </summary>
    public static GitSimResult Checkout(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);
        var pathspecs = ctx.Pathspecs();
        var rest = ctx.Args.Skip(1).ToList();

        // checkout -b <branchname>  — create new branch at HEAD and switch to it.
        if (ctx.Has("-b"))
        {
            var branchName = ctx.ValueAfter("-b")!;
            var headSha = wt.ResolveHead();
            if (headSha is null)
                return GitSimResult.Fatal("cannot create branch: empty HEAD");
            wt.Repo.Refs["refs/heads/" + branchName] = headSha;
            wt.Head.PointAt("refs/heads/" + branchName);
            return GitSimResult.Ok();
        }

        var source = rest.Count > 0 && rest[0] != "--" ? rest[0] : null;

        if (source is not null)
        {
            var sourceSha = ResolveRevision(wt, source);
            if (sourceSha is null)
                return GitSimResult.Fatal($"could not resolve {source}");
            // If source is a known branch name, attach HEAD to that branch.
            if (wt.Repo.Refs.TryGetValue("refs/heads/" + source, out _))
                wt.Head.PointAt("refs/heads/" + source);
            else
                wt.Head.Detach(sourceSha);
            var tree = TreeBuilder.FlattenTree(store, TreeBuilder.ResolveTreeSha(store, sourceSha) ?? sourceSha);
            foreach (var (path, entry) in tree)
            {
                if (!MatchesAny(path, pathspecs))
                    continue;
                if (store.TryGetBlob(entry.Sha, out var blob))
                    WorkingTree.WriteBytes(wt.Root, path, blob.Content);
                index.Set(path, entry);
            }

            return GitSimResult.Ok();
        }

        foreach (var (path, entry) in index.Stage0)
            if (MatchesAny(path, pathspecs) && store.TryGetBlob(entry.Sha, out var blob))
                WorkingTree.WriteBytes(wt.Root, path, blob.Content);
        return GitSimResult.Ok();
    }
}
