using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

/// <summary>Snapshot builders and the path-set diff shared by the diff/status handlers.</summary>
internal static partial class GitSimCommands
{
    internal enum DiffStatus { Added, Modified, Deleted }

    /// <summary>A path→content-sha view of a commit's/index's/working tree's file set.</summary>
    internal sealed class Snapshot(Dictionary<string, string> byPath)
    {
        public IReadOnlyDictionary<string, string> ByPath => byPath;
        public IEnumerable<string> Paths => byPath.Keys;
    }

    public static Snapshot TreeSnapshot(GitObjectStore store, string? treeIsh)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (treeIsh is not null)
            foreach (var (path, entry) in TreeBuilder.FlattenTree(store, TreeBuilder.ResolveTreeSha(store, treeIsh) ?? treeIsh))
                map[path] = entry.Sha;
        return new Snapshot(map);
    }

    public static Snapshot HeadSnapshot(Worktree wt) => TreeSnapshot(wt.Repo.Objects, wt.ResolveHead());

    public static Snapshot IndexSnapshot(GitIndex index)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, entry) in index.Stage0)
            map[path] = entry.Sha;
        return new Snapshot(map);
    }

    /// <summary>
    /// The working-tree content sha of each path in <paramref name="trackedPaths"/> that
    /// still exists on disk (missing files are simply absent → they read as deletions
    /// against a snapshot that has them).
    /// </summary>
    public static Snapshot WorktreeSnapshot(Worktree wt, IEnumerable<string> trackedPaths)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in trackedPaths.Distinct(StringComparer.Ordinal))
            if (WorkingTree.FileExists(wt.Root, path))
                map[path] = WorkingTree.StageBlob(wt.Repo.Objects, wt.Root, path);
        return new Snapshot(map);
    }

    /// <summary>Every path tracked at HEAD or staged in the index.</summary>
    public static IEnumerable<string> TrackedPaths(Worktree wt, GitIndex index) =>
        HeadSnapshot(wt).Paths.Concat(index.Stage0.Select(e => e.Path)).Distinct(StringComparer.Ordinal);

    /// <summary>Path-level diff of two snapshots: additions, modifications, deletions, sorted by path.</summary>
    public static IReadOnlyList<(DiffStatus Status, string Path)> Diff(Snapshot from, Snapshot to)
    {
        var result = new List<(DiffStatus, string)>();
        foreach (var (path, sha) in to.ByPath)
        {
            if (!from.ByPath.TryGetValue(path, out var oldSha))
                result.Add((DiffStatus.Added, path));
            else if (!string.Equals(oldSha, sha, StringComparison.Ordinal))
                result.Add((DiffStatus.Modified, path));
        }

        foreach (var path in from.ByPath.Keys)
            if (!to.ByPath.ContainsKey(path))
                result.Add((DiffStatus.Deleted, path));

        return result.OrderBy(r => r.Item2, StringComparer.Ordinal).ToList();
    }

    /// <summary>Restricts a change list to entries under one of the given pathspecs (prefix/exact); no specs → unchanged.</summary>
    public static IReadOnlyList<(DiffStatus Status, string Path)> FilterByPathspec(
        IReadOnlyList<(DiffStatus Status, string Path)> changes, IReadOnlyList<string> pathspecs) =>
        pathspecs.Count == 0 ? changes : changes.Where(c => MatchesAny(c.Path, pathspecs)).ToList();

    public static bool MatchesAny(string path, IReadOnlyList<string> pathspecs)
    {
        if (pathspecs.Count == 0)
            return true; // no pathspec == match every path (git convention)
        foreach (var spec in pathspecs)
        {
            var s = spec.Replace('\\', '/').TrimEnd('/');
            if (s.Length == 0 || s == "." || path == s || path.StartsWith(s + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Joins paths with NUL (git <c>-z</c>) or newline, with a trailing separator on non-empty NUL output.</summary>
    public static string JoinPaths(IEnumerable<string> paths, bool nul)
    {
        var list = paths.ToList();
        if (list.Count == 0)
            return string.Empty;
        return nul
            ? string.Concat(list.Select(p => p + '\0'))
            : string.Join('\n', list) + '\n';
    }

    private static char StatusLetter(DiffStatus status) => status switch
    {
        DiffStatus.Added => 'A',
        DiffStatus.Modified => 'M',
        _ => 'D',
    };
}
