using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>add</c>: <c>-A</c>/plain (stage tracked updates+deletes and untracked adds),
    /// <c>-u</c> (tracked updates+deletes only, never new files), <c>-f</c> (force
    /// past <c>.gitignore</c>). A trailing <c>-- &lt;paths&gt;</c> restricts to those
    /// pathspecs; the working tree is the real filesystem.
    /// </summary>
    public static GitSimResult Add(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var index = ctx.Index(wt);
        var pathspecs = ctx.Pathspecs();
        var includeUntracked = !ctx.Has("-u"); // -u stages tracked changes only
        var force = ctx.Has("-f");
        var ignore = GitIgnore.Load(wt.Root);
        var tracked = TrackedPaths(wt, index).ToHashSet(StringComparer.Ordinal);

        // Tracked paths: update from the working tree, or stage a deletion when gone.
        foreach (var path in tracked)
        {
            if (pathspecs.Count > 0 && !MatchesAny(path, pathspecs))
                continue;

            if (WorkingTree.FileExists(wt.Root, path))
            {
                var sha = WorkingTree.StageBlob(wt.Repo.Objects, wt.Root, path);
                index.Set(path, new IndexEntry(WorkingTree.ModeOnDisk(wt.Root, path), sha));
            }
            else
            {
                index.Remove(path);
            }
        }

        // New untracked files (skipped for -u, and skipped when ignored unless forced).
        if (includeUntracked)
        {
            foreach (var rel in WorkingTree.EnumerateFiles(wt.Root))
            {
                if (tracked.Contains(rel))
                    continue;
                if (!force && ignore.IsIgnored(rel))
                    continue;
                if (pathspecs.Count > 0 && !MatchesAny(rel, pathspecs))
                    continue;

                var sha = WorkingTree.StageBlob(wt.Repo.Objects, wt.Root, rel);
                index.Set(rel, new IndexEntry(WorkingTree.ModeOnDisk(wt.Root, rel), sha));
            }
        }

        return GitSimResult.Ok();
    }
}
