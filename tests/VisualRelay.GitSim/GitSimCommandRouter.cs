namespace VisualRelay.GitSim;

/// <summary>
/// Dispatches a stripped argv to the handler for its subcommand. An unrecognized
/// subcommand (or a sub-shape a handler declines) throws via
/// <see cref="GitSimContext.Unsupported"/>, so a test hitting an unmodeled command
/// fails loudly and the simulator gets extended rather than quietly worked around.
/// </summary>
internal static class GitSimCommandRouter
{
    public static GitSimResult Dispatch(GitSimContext ctx) => ctx.Command switch
    {
        "rev-parse" => GitSimCommands.RevParse(ctx),
        "ls-files" => GitSimCommands.LsFiles(ctx),
        "diff" => GitSimCommands.Diff(ctx),
        "diff-tree" => GitSimCommands.DiffTree(ctx),
        "status" => GitSimCommands.Status(ctx),
        "add" => GitSimCommands.Add(ctx),
        "commit" => GitSimCommands.Commit(ctx),
        "commit-tree" => GitSimCommands.CommitTree(ctx),
        "reset" => GitSimCommands.Reset(ctx),
        "restore" => GitSimCommands.Restore(ctx),
        "checkout" => GitSimCommands.Checkout(ctx),
        "stash" => GitSimCommands.Stash(ctx),
        "read-tree" => GitSimCommands.ReadTree(ctx),
        "write-tree" => GitSimCommands.WriteTree(ctx),
        "update-ref" => GitSimCommands.UpdateRef(ctx),
        "rm" => GitSimCommands.Rm(ctx),
        "cat-file" => GitSimCommands.CatFile(ctx),
        "merge-base" => GitSimCommands.MergeBase(ctx),
        "rev-list" => GitSimCommands.RevList(ctx),
        "ls-tree" => GitSimCommands.LsTree(ctx),
        "bundle" => GitSimCommands.Bundle(ctx),
        "fetch" => GitSimCommands.Fetch(ctx),
        "cherry-pick" => GitSimCommands.CherryPick(ctx),
        "log" => GitSimCommands.Log(ctx),
        "merge" => GitSimCommands.Merge(ctx),
        "worktree" => GitSimCommands.Worktree(ctx),
        "config" => GitSimCommands.Config(ctx),
        "init" => GitSimCommands.Init(ctx),
        "symbolic-ref" => GitSimCommands.SymbolicRef(ctx),
        "var" => GitSimCommands.Var(ctx),
        "tag" => GitSimCommands.Tag(ctx),
        "check-ignore" => GitSimCommands.CheckIgnore(ctx),
        "update-index" => GitSimCommands.UpdateIndex(ctx),
        _ => ctx.Unsupported(),
    };
}
