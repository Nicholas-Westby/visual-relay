namespace VisualRelay.GitSim.State;

/// <summary>
/// Converts between the flat index/path world and the nested tree-object world:
/// builds trees from index entries, flattens trees back to path→entry maps, loads
/// trees into an index, and resolves a single path inside a tree. All paths are
/// repo-relative with <c>/</c> separators.
/// </summary>
internal static class TreeBuilder
{
    /// <summary>Writes the stage-0 index as a (possibly nested) tree and returns the root tree sha.</summary>
    public static string BuildTreeFromIndex(GitObjectStore store, GitIndex index) =>
        BuildTree(store, index.Stage0.Select(e => (e.Path, e.Entry)).ToList());

    /// <summary>Builds a tree from an explicit path→entry set and returns the root tree sha.</summary>
    public static string BuildTree(GitObjectStore store, IReadOnlyList<(string Path, IndexEntry Entry)> entries)
    {
        var files = new List<GitTreeEntry>();
        var subdirs = new Dictionary<string, List<(string Path, IndexEntry Entry)>>(StringComparer.Ordinal);

        foreach (var (path, entry) in entries)
        {
            var slash = path.IndexOf('/');
            if (slash < 0)
            {
                files.Add(new GitTreeEntry(entry.Mode, path, entry.Sha, GitObjectKind.Blob));
            }
            else
            {
                var dir = path[..slash];
                var rest = path[(slash + 1)..];
                if (!subdirs.TryGetValue(dir, out var list))
                    subdirs[dir] = list = [];
                list.Add((rest, entry));
            }
        }

        foreach (var (dir, list) in subdirs)
        {
            var subSha = BuildTree(store, list);
            files.Add(new GitTreeEntry("40000", dir, subSha, GitObjectKind.Tree));
        }

        return store.PutTree(new GitTree(files));
    }

    /// <summary>Recursively flattens <paramref name="treeSha"/> to a path→entry map (empty if it is not a tree).</summary>
    public static Dictionary<string, IndexEntry> FlattenTree(GitObjectStore store, string treeSha)
    {
        var result = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        Walk(store, treeSha, string.Empty, result);
        return result;
    }

    private static void Walk(GitObjectStore store, string treeSha, string prefix, Dictionary<string, IndexEntry> sink)
    {
        if (!store.TryGetTree(treeSha, out var tree))
            return;
        foreach (var e in tree.Entries)
        {
            var path = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
            if (e.Kind == GitObjectKind.Tree)
                Walk(store, e.Sha, path, sink);
            else
                sink[path] = new IndexEntry(e.Mode, e.Sha);
        }
    }

    /// <summary>Replaces the index stage-0 contents with the flattened tree of <paramref name="treeIsh"/>.</summary>
    public static void ReadTreeIntoIndex(GitObjectStore store, GitIndex index, string treeIsh)
    {
        index.Clear();
        var treeSha = ResolveTreeSha(store, treeIsh);
        if (treeSha is null)
            return;
        foreach (var (path, entry) in FlattenTree(store, treeSha))
            index.Set(path, entry);
    }

    /// <summary>Maps a tree-ish (a tree sha, or a commit sha whose root tree is used) to a tree sha.</summary>
    public static string? ResolveTreeSha(GitObjectStore store, string treeIsh)
    {
        if (store.IsTree(treeIsh))
            return treeIsh;
        return store.TryGetCommit(treeIsh, out var commit) ? commit.TreeSha : null;
    }

    /// <summary>Resolves a single repo-relative path inside a tree-ish to its entry, or null when absent.</summary>
    public static IndexEntry? Lookup(GitObjectStore store, string treeIsh, string path)
    {
        var treeSha = ResolveTreeSha(store, treeIsh);
        return treeSha is not null && FlattenTree(store, treeSha).TryGetValue(path, out var entry) ? entry : null;
    }
}
