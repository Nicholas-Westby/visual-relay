using System.Globalization;
using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>commit -m &lt;msg&gt;</c> (± <c>--allow-empty</c>): builds a tree from the
    /// index, consults <see cref="GitSim.PreCommitHook"/> (a reject → non-zero exit
    /// with the message in output, exactly what <c>GitCommitter</c> parses), then
    /// records the commit and advances HEAD. Author/committer come from
    /// <c>GIT_AUTHOR_*</c>/<c>GIT_COMMITTER_*</c> when present.
    /// </summary>
    public static GitSimResult Commit(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var msg = ctx.ValueAfter("-m");
        if (msg is null)
            return ctx.Unsupported();

        var index = ctx.Index(wt);
        if (index.HasUnmerged)
            return GitSimResult.Code(1, "error: committing is not possible because you have unmerged files.\n");

        var store = wt.Repo.Objects;
        var pathspecs = ctx.Pathspecs();
        var headSha = wt.ResolveHead();

        string treeSha;
        if (pathspecs.Count > 0)
        {
            // Pathspec commit: overlay the pathspec entries from the index onto
            // HEAD. All OTHER paths stay frozen at their HEAD version — only the
            // named pathspecs pick up staged changes. This matches real git's
            // `git commit -- <paths>` semantics.
            var entries = headSha is not null
                ? TreeBuilder.FlattenTree(store, TreeBuilder.ResolveTreeSha(store, headSha) ?? headSha)
                : new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

            foreach (var path in pathspecs)
            {
                var match = index.Stage0.FirstOrDefault(e =>
                    string.Equals(e.Path, path, StringComparison.Ordinal));
                if (match.Path is not null)
                    entries[match.Path] = match.Entry;
            }

            treeSha = TreeBuilder.BuildTree(
                store,
                entries.Select(kvp => (kvp.Key, kvp.Value)).ToList());

            // "Nothing to commit" check for the pathspec case: compare each
            // pathspec entry in the tree against its HEAD counterpart.
            if (!ctx.Has("--allow-empty")
                && headSha is not null
                && store.TryGetCommit(headSha, out var head)
                && string.Equals(head.TreeSha, treeSha, StringComparison.Ordinal))
                return GitSimResult.Code(1, "nothing to commit, working tree clean\n");
        }
        else
        {
            treeSha = TreeBuilder.BuildTreeFromIndex(store, index);
        }

        if (ctx.PreCommitHook is not null)
        {
            var staged = index.Stage0.Select(e => e.Path).ToList();
            var verdict = ctx.PreCommitHook(new GitSimCommitRequest(staged, msg, ctx.Environment));
            if (!verdict.Accepted)
                return GitSimResult.Code(1, verdict.Message.TrimEnd('\n') + "\n");
        }

        if (!ctx.Has("--allow-empty")
            && headSha is not null
            && store.TryGetCommit(headSha, out var headCommit)
            && string.Equals(headCommit.TreeSha, treeSha, StringComparison.Ordinal))
            return GitSimResult.Code(1, "nothing to commit, working tree clean\n");

        var when = wt.Repo.NextTimestamp();
        var author = PersonFromEnv(ctx, wt, "GIT_AUTHOR", when);
        var committer = PersonFromEnv(ctx, wt, "GIT_COMMITTER", when);
        var parents = headSha is null ? Array.Empty<string>() : [headSha];
        var commitSha = store.PutCommit(new GitCommit(treeSha, parents, author, committer, msg));
        wt.MoveHeadTo(commitSha);

        var branch = wt.Head.SymbolicRef?["refs/heads/".Length..] ?? "detached";
        return GitSimResult.Ok($"[{branch} {commitSha[..7]}] {Subject(msg)}\n");
    }

    /// <summary>
    /// <c>commit-tree &lt;tree&gt; [-p &lt;parent&gt;]… (-F &lt;file&gt; | -m &lt;msg&gt;)</c>:
    /// records a commit from an explicit tree and parents, honoring the author/committer
    /// environment. Bypasses hooks (as real git does) and does NOT move HEAD; stdout is
    /// the new commit sha.
    /// </summary>
    public static GitSimResult CommitTree(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        if (ctx.Args.Count < 2)
            return ctx.Unsupported();

        var treeSha = TreeBuilder.ResolveTreeSha(store, ctx.Args[1]);
        if (treeSha is null)
            return GitSimResult.Fatal($"not a valid object name {ctx.Args[1]}");

        var parents = new List<string>();
        for (var i = 1; i < ctx.Args.Count - 1; i++)
            if (ctx.Args[i] == "-p")
            {
                var resolved = ResolveRevision(wt, ctx.Args[i + 1]) ?? ctx.Args[i + 1];
                parents.Add(resolved);
            }

        string? msg = ctx.ValueAfter("-m");
        if (msg is null && ctx.ValueAfter("-F") is { } file && File.Exists(file))
            msg = File.ReadAllText(file);
        if (msg is null)
            return ctx.Unsupported();

        var when = wt.Repo.NextTimestamp();
        var author = PersonFromEnv(ctx, wt, "GIT_AUTHOR", when);
        var committer = PersonFromEnv(ctx, wt, "GIT_COMMITTER", when);
        var commitSha = store.PutCommit(new GitCommit(treeSha, parents, author, committer, msg));
        return GitSimResult.Ok(commitSha + "\n");
    }

    private static GitPerson PersonFromEnv(GitSimContext ctx, Worktree wt, string prefix, DateTimeOffset fallback)
    {
        var name = ctx.Environment.GetValueOrDefault($"{prefix}_NAME") ?? wt.Repo.Identity.Name;
        var email = ctx.Environment.GetValueOrDefault($"{prefix}_EMAIL") ?? wt.Repo.Identity.Email;
        var when = ParseGitDate(ctx.Environment.GetValueOrDefault($"{prefix}_DATE")) ?? fallback;
        return new GitPerson(name, email, when);
    }

    private static DateTimeOffset? ParseGitDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
            return iso;

        // "unixseconds +zone" form.
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && long.TryParse(parts[0], out var seconds))
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (parts.Length == 2 && parts[1].Length == 5
                && int.TryParse(parts[1].AsSpan(0, 3), out var hh)
                && int.TryParse(parts[1].AsSpan(3, 2), out var mm))
                return when.ToOffset(new TimeSpan(hh, hh < 0 ? -mm : mm, 0));
            return when;
        }

        return null;
    }

    private static string Subject(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline];
    }
}
