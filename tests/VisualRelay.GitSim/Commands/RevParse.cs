using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>rev-parse</c>: <c>--is-inside-work-tree</c>, <c>--show-toplevel</c>, and
    /// revision resolution (<c>HEAD</c>, <c>HEAD~N</c>, <c>&lt;sha&gt;^{commit}</c>,
    /// refs). <c>--verify --quiet</c> turns a non-resolving revision into a silent
    /// exit 1 (the unborn-HEAD probe); otherwise it is a fatal 128.
    /// </summary>
    public static GitSimResult RevParse(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        if (ctx.Has("--is-inside-work-tree"))
            return GitSimResult.Ok("true\n");

        if (ctx.Has("--show-toplevel"))
            return GitSimResult.Ok(wt.Root + "\n");

        var quiet = ctx.Has("--quiet");
        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));
        if (operand is null)
            return ctx.Unsupported();

        var sha = ResolveRevision(wt, operand);
        if (sha is not null)
            return GitSimResult.Ok(sha + "\n");

        return quiet
            ? GitSimResult.Code(1)
            : GitSimResult.Fatal(
                $"ambiguous argument '{operand}': unknown revision or path not in the working tree.");
    }
}
