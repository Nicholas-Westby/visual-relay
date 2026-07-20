using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>merge (--no-ff)? --no-edit &lt;branch&gt;</c>: merges the specified branch
    /// into the current branch. Fast-forwards when possible (HEAD is an ancestor of
    /// the merged branch) unless <c>--no-ff</c> forces a merge commit. When no merge
    /// commit is needed because the merged branch is already an ancestor of HEAD the
    /// command reports "Already up to date." The working tree must be clean (index
    /// tree matches HEAD's tree and no unmerged entries).
    /// </summary>
    public static GitSimResult Merge(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var branchArg = ctx.Args.LastOrDefault(a => !a.StartsWith('-') && a != "--no-ff" && a != "--no-edit" && a != "--abort");
        if (branchArg is null)
            return ctx.Unsupported();

        var branchTip = ResolveRevision(wt, branchArg);
        if (branchTip is null)
            return GitSimResult.Fatal($"merge: {branchArg} - not something we can merge");

        var headSha = wt.ResolveHead();
        if (headSha is null)
            return GitSimResult.Fatal("merge: nothing to merge - HEAD is unborn");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);

        // Verify working tree is clean: no unmerged entries and index matches HEAD tree.
        if (index.HasUnmerged)
            return GitSimResult.Code(1, "error: Merging is not possible because you have unmerged files.\n");

        var indexTreeSha = TreeBuilder.BuildTreeFromIndex(store, index);
        if (store.TryGetCommit(headSha, out var headCommit)
            && !string.Equals(headCommit.TreeSha, indexTreeSha, StringComparison.Ordinal))
            return GitSimResult.Code(1, "error: Your local changes would be overwritten by merge.\n");

        // Already up-to-date: merged branch is already an ancestor of HEAD.
        if (IsAncestor(store, branchTip, headSha))
            return GitSimResult.Ok("Already up to date.\n");

        var noFf = ctx.Has("--no-ff");

        // Fast-forward: HEAD is an ancestor of the merged branch (and no --no-ff).
        if (!noFf && IsAncestor(store, headSha, branchTip))
        {
            wt.MoveHeadTo(branchTip);
            return GitSimResult.Ok("Updating ...\n");
        }

        // Create a merge commit with two parents.
        var message = ctx.Has("--no-edit")
            ? $"Merge branch '{branchArg}'"
            : ctx.ValueAfter("-m") ?? $"Merge branch '{branchArg}'";

        var when = wt.Repo.NextTimestamp();
        var author = PersonFromEnv(ctx, wt, "GIT_AUTHOR", when);
        var committer = PersonFromEnv(ctx, wt, "GIT_COMMITTER", when);
        var parents = new List<string> { headSha, branchTip };

        // Use HEAD's tree for the merge commit (simplified — no content-level merge).
        var mergeTreeSha = headCommit?.TreeSha ?? indexTreeSha;

        var mergeSha = store.PutCommit(new GitCommit(mergeTreeSha, parents, author, committer, message));
        wt.MoveHeadTo(mergeSha);

        var branch = wt.Head.SymbolicRef?["refs/heads/".Length..] ?? "detached";
        return GitSimResult.Ok($"[{branch} {mergeSha[..7]}] {Subject(message)}\n");
    }
}
