using System.Text.RegularExpressions;
using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>stash push -u -m &lt;tag&gt; [-- &lt;paths&gt;]</c> (capture working-tree +
    /// untracked changes vs HEAD, then reset the working tree to HEAD), <c>stash list</c>
    /// (<c>stash@{n}: On branch: tag</c>), <c>stash apply &lt;ref&gt;</c> (re-lay a stash;
    /// a missing ref is a non-zero exit), and <c>stash drop &lt;ref&gt;</c>.
    /// </summary>
    public static GitSimResult Stash(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        return ctx.Args.ElementAtOrDefault(1) switch
        {
            "push" => StashPush(ctx, wt),
            "list" => StashList(wt),
            "apply" => StashApply(ctx, wt),
            "drop" => StashDrop(ctx, wt),
            _ => ctx.Unsupported(),
        };
    }

    private static GitSimResult StashPush(GitSimContext ctx, Worktree wt)
    {
        var store = wt.Repo.Objects;
        var head = HeadSnapshot(wt).ByPath;
        var pathspecs = ctx.Pathspecs();
        var ignore = GitIgnore.Load(wt.Root);
        var index = ctx.Index(wt);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var deletions = new HashSet<string>(StringComparer.Ordinal);
        var trackedPaths = TrackedPaths(wt, index).ToHashSet(StringComparer.Ordinal);

        foreach (var path in trackedPaths.Where(p => MatchesAny(p, pathspecs)))
        {
            if (WorkingTree.FileExists(wt.Root, path))
            {
                var diskSha = WorkingTree.StageBlob(store, wt.Root, path);
                if (!head.TryGetValue(path, out var headSha) || headSha != diskSha)
                    contents[path] = WorkingTree.ReadBytes(wt.Root, path);
            }
            else if (head.ContainsKey(path))
            {
                deletions.Add(path);
            }
        }

        foreach (var rel in WorkingTree.EnumerateFiles(wt.Root))
        {
            if (trackedPaths.Contains(rel) || ignore.IsIgnored(rel) || !MatchesAny(rel, pathspecs))
                continue;
            contents[rel] = WorkingTree.ReadBytes(wt.Root, rel);
        }

        if (contents.Count == 0 && deletions.Count == 0)
            return GitSimResult.Code(1, "No local changes to save\n");

        // Reset the working tree to HEAD: restore tracked, delete captured untracked.
        foreach (var path in contents.Keys.Concat(deletions))
            if (head.TryGetValue(path, out var sha) && store.TryGetBlob(sha, out var blob))
                WorkingTree.WriteBytes(wt.Root, path, blob.Content);
            else
                WorkingTree.Delete(wt.Root, path);

        var headSha0 = wt.ResolveHead();
        if (headSha0 is null)
            index.Clear();
        else
            TreeBuilder.ReadTreeIntoIndex(store, index, headSha0);

        var tag = ctx.ValueAfter("-m") ?? string.Empty;
        wt.Stashes.Insert(0, new StashEntry(tag, BranchName(wt), contents, deletions));
        return GitSimResult.Ok($"Saved working directory and index state On {BranchName(wt)}: {tag}\n");
    }

    private static GitSimResult StashList(Worktree wt)
    {
        var lines = wt.Stashes.Select((s, i) => $"stash@{{{i}}}: On {s.Branch}: {s.Message}");
        return GitSimResult.Ok(JoinPaths(lines, nul: false));
    }

    private static GitSimResult StashApply(GitSimContext ctx, Worktree wt)
    {
        var n = StashIndex(ctx.Args.ElementAtOrDefault(2));
        if (n < 0 || n >= wt.Stashes.Count)
            return GitSimResult.Fatal($"{ctx.Args.ElementAtOrDefault(2)} is not a valid reference");

        var entry = wt.Stashes[n];
        foreach (var (path, bytes) in entry.Contents)
            WorkingTree.WriteBytes(wt.Root, path, bytes);
        foreach (var path in entry.Deletions)
            WorkingTree.Delete(wt.Root, path);
        return GitSimResult.Ok();
    }

    private static GitSimResult StashDrop(GitSimContext ctx, Worktree wt)
    {
        var n = StashIndex(ctx.Args.ElementAtOrDefault(2));
        if (n < 0 || n >= wt.Stashes.Count)
            return GitSimResult.Fatal($"{ctx.Args.ElementAtOrDefault(2)} is not a valid reference");
        wt.Stashes.RemoveAt(n);
        return GitSimResult.Ok();
    }

    private static int StashIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return 0;
        var m = Regex.Match(reference, @"stash@\{(\d+)\}");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static string BranchName(Worktree wt) =>
        wt.Head.SymbolicRef?.StartsWith("refs/heads/", StringComparison.Ordinal) == true
            ? wt.Head.SymbolicRef["refs/heads/".Length..]
            : "(no branch)";
}
