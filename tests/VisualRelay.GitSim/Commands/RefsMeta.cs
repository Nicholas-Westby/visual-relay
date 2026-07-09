using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>symbolic-ref --short --quiet HEAD</c>: the current branch's short name, or a
    /// silent exit 1 when HEAD is detached.
    /// </summary>
    public static GitSimResult SymbolicRef(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        if (wt.Head.IsDetached || wt.Head.SymbolicRef is null)
            return GitSimResult.Code(1);

        var name = wt.Head.SymbolicRef;
        if (ctx.Has("--short") && name.StartsWith("refs/heads/", StringComparison.Ordinal))
            name = name["refs/heads/".Length..];
        return GitSimResult.Ok(name + "\n");
    }

    /// <summary><c>var GIT_AUTHOR_IDENT</c>: <c>Name &lt;email&gt; &lt;unixts&gt; &lt;tz&gt;</c> from the repo identity.</summary>
    public static GitSimResult Var(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");
        if (ctx.Args.ElementAtOrDefault(1) != "GIT_AUTHOR_IDENT")
            return ctx.Unsupported();

        var id = wt.Repo.Identity;
        return GitSimResult.Ok($"{id.Name} <{id.Email}> {id.When.ToUnixTimeSeconds()} {FormatTz(id.When.Offset)}\n");
    }

    /// <summary><c>tag -f &lt;name&gt; &lt;sha&gt;</c>: (re)points <c>refs/tags/&lt;name&gt;</c> at the resolved commit.</summary>
    public static GitSimResult Tag(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var positional = ctx.Args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (positional.Count < 2)
            return ctx.Unsupported();

        var target = ResolveRevision(wt, positional[1]);
        if (target is null)
            return GitSimResult.Fatal($"Failed to resolve '{positional[1]}' as a valid ref.");
        wt.Repo.Refs[$"refs/tags/{positional[0]}"] = target;
        return GitSimResult.Ok();
    }

    private static string FormatTz(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return $"{sign}{abs.Hours:D2}{abs.Minutes:D2}";
    }
}
