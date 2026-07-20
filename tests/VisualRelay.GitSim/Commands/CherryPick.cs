using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>cherry-pick -n &lt;sha&gt;</c>: applies the commit's diff-vs-first-parent onto
    /// the working tree + index without committing. A path whose on-disk content differs
    /// from BOTH the parent and the picked version is a conflict — it lands as unmerged
    /// index stages (surfaced by <c>ls-files -u</c>) and a non-zero exit.
    /// <c>cherry-pick --quit</c> clears the sequencer flag, leaving any unmerged entries.
    /// </summary>
    public static GitSimResult CherryPick(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        if (ctx.Has("--quit"))
        {
            wt.CherryPickInProgress = false;
            return GitSimResult.Ok();
        }

        if (!ctx.Has("-n"))
            return ctx.Unsupported();

        var operand = ctx.Args.Skip(1).LastOrDefault(a => !a.StartsWith('-'));
        var sha = operand is null ? null : ResolveRevision(wt, operand);
        if (sha is null || !wt.Repo.Objects.TryGetCommit(sha, out var commit))
            return GitSimResult.Fatal($"bad revision '{operand}'");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);
        var parentTree = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var parentEntries = parentTree is null
            ? new Dictionary<string, IndexEntry>(StringComparer.Ordinal)
            : TreeBuilder.FlattenTree(store, TreeBuilder.ResolveTreeSha(store, parentTree) ?? parentTree);
        var theirEntries = TreeBuilder.FlattenTree(store, commit.TreeSha);
        var conflict = false;

        foreach (var (status, path) in Diff(TreeSnapshot(store, parentTree), TreeSnapshot(store, commit.TreeSha)))
        {
            var diskSha = WorkingTree.FileExists(wt.Root, path) ? WorkingTree.StageBlob(store, wt.Root, path) : null;
            var baseSha = parentEntries.TryGetValue(path, out var baseEntry) ? baseEntry.Sha : null;

            if (status == DiffStatus.Deleted)
            {
                if (diskSha is null || diskSha == baseSha)
                {
                    WorkingTree.Delete(wt.Root, path);
                    index.Remove(path);
                }
                else
                {
                    conflict = true;
                    StageConflict(index, path, baseEntry?.Mode, baseSha, diskSha, null, null);
                    WriteConflictFile(wt, path, diskSha, null, theirs: null);
                }

                continue;
            }

            var theirs = theirEntries[path];
            if (diskSha is not null && diskSha != baseSha && diskSha != theirs.Sha)
            {
                conflict = true;
                StageConflict(index, path, parentEntries.GetValueOrDefault(path)?.Mode, baseSha, diskSha, theirs.Mode, theirs.Sha);
                WriteConflictFile(wt, path, diskSha, theirs.Sha, theirs: commit);
            }
            else
            {
                if (store.TryGetBlob(theirs.Sha, out var blob))
                    WorkingTree.WriteBytes(wt.Root, path, blob.Content);
                index.Remove(path);
                index.Set(path, new IndexEntry(theirs.Mode, theirs.Sha));
            }
        }

        wt.CherryPickInProgress = true;
        return conflict
            ? GitSimResult.Code(1, "error: could not apply commit\nhint: resolve conflicts then commit the result\n")
            : GitSimResult.Ok();
    }

    private static void StageConflict(
        GitIndex index, string path, string? baseMode, string? baseSha, string? oursSha, string? theirMode, string? theirsSha)
    {
        index.Remove(path);
        if (baseSha is not null)
            index.Set(path, new IndexEntry(baseMode ?? "100644", baseSha, 1));
        if (oursSha is not null)
            index.Set(path, new IndexEntry("100644", oursSha, 2));
        if (theirsSha is not null)
            index.Set(path, new IndexEntry(theirMode ?? "100644", theirsSha, 3));
    }

    private static void WriteConflictFile(Worktree wt, string path, string? oursSha,
        string? theirsSha, GitCommit? theirs)
    {
        var store = wt.Repo.Objects;
        var theirsLabel = theirs is not null ? $"cherry-pick of {theirs.Message.Split('\n', 2)[0]}" : "theirs";
        using var ms = new MemoryStream();
        using var sw = new StreamWriter(ms, System.Text.Encoding.UTF8);
        sw.WriteLine("<<<<<<< HEAD");
        if (oursSha is not null && store.TryGetBlob(oursSha, out var oursBlob))
            sw.Write(System.Text.Encoding.UTF8.GetString(oursBlob.Content));
        sw.WriteLine("=======");
        if (theirsSha is not null && store.TryGetBlob(theirsSha, out var theirsBlob))
            sw.Write(System.Text.Encoding.UTF8.GetString(theirsBlob.Content));
        sw.WriteLine($">>>>>>> {theirsLabel}");
        sw.Flush();
        WorkingTree.WriteBytes(wt.Root, path, ms.ToArray());
    }
}
