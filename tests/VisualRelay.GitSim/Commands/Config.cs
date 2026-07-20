using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary><c>init</c> / <c>init -b &lt;branch&gt;</c>: registers the repo at the root (re-init is a no-op).</summary>
    public static GitSimResult Init(GitSimContext ctx)
    {
        var branch = ctx.ValueAfter("-b") ?? ctx.ValueAfter("--initial-branch") ?? "main";
        GitSimRegistry.Init(ctx.Root, branch);
        return GitSimResult.Ok($"Initialized empty Git repository in {GitSimRegistry.Normalize(ctx.Root)}/.git/\n");
    }

    /// <summary>
    /// <c>config core.hooksPath &lt;dir&gt;</c> (set), <c>config --default &lt;fallback&gt;
    /// core.hooksPath</c> (get-or-default), and <c>config user.name/user.email &lt;v&gt;</c>
    /// (identity). Other keys are unsupported.
    /// </summary>
    public static GitSimResult Config(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        if (ctx.Has("--default"))
        {
            var fallback = ctx.ValueAfter("--default");
            var key = ctx.Args[^1];
            if (key == "core.hooksPath")
                return GitSimResult.Ok((wt.Repo.HooksPath ?? fallback ?? ".git/hooks") + "\n");
            return ctx.Unsupported();
        }

        var positional = ctx.Args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (positional.Count == 2)
        {
            switch (positional[0])
            {
                case "core.hooksPath":
                    wt.Repo.HooksPath = positional[1];
                    return GitSimResult.Ok();
                case "core.fileMode":
                    wt.Repo.CoreFileMode = positional[1];
                    return GitSimResult.Ok();
                case "user.name":
                    wt.Repo.Identity = wt.Repo.Identity with { Name = positional[1] };
                    return GitSimResult.Ok();
                case "user.email":
                    wt.Repo.Identity = wt.Repo.Identity with { Email = positional[1] };
                    return GitSimResult.Ok();
                default:
                    // Silently accept unknown config keys — tests set things like
                    // commit.gpgsign that don't affect the in-memory model.
                    return GitSimResult.Ok();
            }
        }

        if (positional is ["core.hooksPath"])
            return wt.Repo.HooksPath is { } path ? GitSimResult.Ok(path + "\n") : GitSimResult.Code(1);

        return ctx.Unsupported();
    }

    /// <summary>
    /// <c>check-ignore -- &lt;paths&gt;</c>: prints the subset of paths ignored by the
    /// repo-root <c>.gitignore</c>; exit 0 when any are ignored, exit 1 when none are.
    /// </summary>
    public static GitSimResult CheckIgnore(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var ignore = GitIgnore.Load(wt.Root);
        var paths = ctx.Pathspecs();
        if (paths.Count == 0)
            paths = ctx.Args.Skip(1).Where(a => !a.StartsWith('-') && a != "--").ToList();

        var ignored = paths.Where(p => ignore.IsIgnored(p.Replace('\\', '/').TrimStart('/'))).ToList();
        return ignored.Count == 0
            ? GitSimResult.Code(1)
            : GitSimResult.Ok(string.Join('\n', ignored) + "\n");
    }
}
