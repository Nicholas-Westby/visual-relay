using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>log</c>: <c>-1 --format=%B &lt;sha&gt;</c> (one commit), <c>--reverse
    /// --format=&lt;fmt&gt; &lt;range&gt;</c> (a range oldest→newest), and <c>--follow -1
    /// --format=%cI -- &lt;path&gt;</c> (the newest commit touching a path). Each commit's
    /// formatted output is newline-terminated, matching git's tformat separator.
    /// </summary>
    public static GitSimResult Log(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        var format = ctx.Args.FirstOrDefault(a => a.StartsWith("--format=", StringComparison.Ordinal))
            ?["--format=".Length..] ?? "%H";
        var pathspecs = ctx.Pathspecs();

        // --follow -1 --format=%cI -- <path>: newest commit that changed <path>.
        if (ctx.Has("--follow") && pathspecs.Count > 0)
        {
            var head = wt.ResolveHead();
            var newest = head is null
                ? null
                : OrderByDateDescending(store, ReachableFrom(store, head))
                    .FirstOrDefault(s => pathspecs.Any(p => CommitChangedPath(store, s, p, wt.Root)));
            return newest is null ? GitSimResult.Ok() : GitSimResult.Ok(FormatCommit(store, newest, format) + "\n");
        }

        var revs = ctx.Args.Skip(1).Where(a => !a.StartsWith('-') && a != "--").ToList();
        var range = revs.LastOrDefault() ?? "HEAD";

        IReadOnlyList<string> shas;
        var dots = range.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var baseRev = ResolveRevision(wt, range[..dots]);
            var tip = ResolveRevision(wt, range[(dots + 2)..]);
            shas = baseRev is null || tip is null ? [] : RangeCommits(wt, baseRev, tip);
        }
        else
        {
            var tip = ResolveRevision(wt, range);
            shas = tip is null ? [] : OrderByDateDescending(store, ReachableFrom(store, tip));
        }

        if (ctx.Has("-1"))
            shas = shas.Take(1).ToList();
        if (ctx.Has("--reverse"))
            shas = shas.Reverse().ToList();

        return GitSimResult.Ok(string.Concat(shas.Select(s => FormatCommit(store, s, format) + "\n")));
    }

    private static bool CommitChangedPath(GitObjectStore store, string sha, string path, string? repoRoot)
    {
        if (!store.TryGetCommit(sha, out var commit))
            return false;
        // If the path is absolute, make it relative to the repo root so it
        // matches the repo-relative paths stored in tree objects.
        var relPath = path;
        if (Path.IsPathRooted(path) && repoRoot is not null)
        {
            var normalizedRoot = repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(normalizedRoot, StringComparison.Ordinal))
                relPath = path[normalizedRoot.Length..].Replace('\\', '/');
        }
        var current = TreeBuilder.Lookup(store, commit.TreeSha, relPath)?.Sha;
        var previous = commit.Parents.Count > 0 ? TreeBuilder.Lookup(store, commit.Parents[0], relPath)?.Sha : null;
        return !string.Equals(current, previous, StringComparison.Ordinal);
    }
}
