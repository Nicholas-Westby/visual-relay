using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>worktree add --detach --quiet &lt;path&gt; HEAD</c> (materialize HEAD's tree at
    /// a new linked worktree sharing this repo's object store, with its own detached
    /// HEAD + index), <c>worktree remove --force &lt;path&gt;</c> (unregister it AND
    /// delete its on-disk directory, exactly as real git does), and
    /// <c>worktree prune</c> (no-op).
    /// </summary>
    public static GitSimResult Worktree(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var sub = ctx.Args.ElementAtOrDefault(1);
        return sub switch
        {
            "add" => WorktreeAdd(ctx, wt),
            "remove" => WorktreeRemove(ctx),
            "prune" => GitSimResult.Ok(),
            _ => ctx.Unsupported(),
        };
    }

    private static GitSimResult WorktreeAdd(GitSimContext ctx, Worktree wt)
    {
        var operands = ctx.Args.Skip(2).Where(a => !a.StartsWith('-')).ToList();
        if (operands.Count == 0)
            return ctx.Unsupported();

        var worktreePath = operands[0];
        var commitish = operands.Count > 1 ? operands[1] : "HEAD";
        var headSha = ResolveRevision(wt, commitish);
        if (headSha is null)
            return GitSimResult.Fatal($"invalid reference: {commitish}");

        Directory.CreateDirectory(worktreePath);
        var store = wt.Repo.Objects;
        foreach (var (path, entry) in TreeBuilder.FlattenTree(store, TreeBuilder.ResolveTreeSha(store, headSha) ?? headSha))
            if (store.TryGetBlob(entry.Sha, out var blob))
                WorkingTree.WriteBytes(worktreePath, path, blob.Content);

        var linked = GitSimRegistry.AddLinked(worktreePath, wt.Repo, headSha);
        TreeBuilder.ReadTreeIntoIndex(store, linked.Index, headSha);
        return GitSimResult.Ok();
    }

    private static GitSimResult WorktreeRemove(GitSimContext ctx)
    {
        var operands = ctx.Args.Skip(2).Where(a => !a.StartsWith('-')).ToList();
        if (operands.Count == 0)
            return ctx.Unsupported();

        var path = operands[0];
        GitSimRegistry.Remove(path);
        // Real `git worktree remove --force` deletes the working-tree directory, not
        // just the admin entry. Match that so callers that assert the on-disk worktree
        // is gone (e.g. PlanningWorktree cleanup) see identical behavior.
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        return GitSimResult.Ok();
    }
}
