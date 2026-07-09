using System.Text.Json;
using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    // Serialized bundle payload (opaque JSON, per the task spec). Objects re-hash to
    // their original shas on import, so the recorded tip resolves after a fetch.
    private sealed record BundleDto(string Ref, string Tip, List<BundleCommit> Commits, List<BundleTree> Trees, Dictionary<string, string> Blobs);
    private sealed record BundleCommit(string Sha, string Tree, List<string> Parents, BundlePerson Author, BundlePerson Committer, string Message);
    private sealed record BundlePerson(string Name, string Email, long Unix, int OffsetMinutes);
    private sealed record BundleTree(string Sha, List<BundleEntry> Entries);
    private sealed record BundleEntry(string Mode, string Name, string Sha, GitObjectKind Kind);

    /// <summary>
    /// <c>bundle create &lt;path&gt; &lt;ref&gt; ^&lt;base&gt;</c> (serialize the closure
    /// reachable from the ref to a JSON file) and <c>bundle verify &lt;path&gt;</c>
    /// (validate that file).
    /// </summary>
    public static GitSimResult Bundle(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var sub = ctx.Args.ElementAtOrDefault(1);
        if (sub == "verify")
        {
            var vp = ctx.Args.ElementAtOrDefault(2);
            return vp is not null && File.Exists(vp) && TryReadBundle(vp) is not null
                ? GitSimResult.Ok($"The bundle at {vp} is okay\n")
                : GitSimResult.Code(1, "error: not a valid bundle\n");
        }

        if (sub != "create")
            return ctx.Unsupported();

        var path = ctx.Args.ElementAtOrDefault(2);
        var refName = ctx.Args.ElementAtOrDefault(3);
        if (path is null || refName is null)
            return ctx.Unsupported();
        var tip = ResolveRevision(wt, refName);
        if (tip is null)
            return GitSimResult.Fatal("Refusing to create empty bundle.");

        File.WriteAllText(path, JsonSerializer.Serialize(BuildBundle(wt.Repo.Objects, refName, tip)));
        return GitSimResult.Ok($"Created bundle at {path}\n");
    }

    /// <summary><c>fetch &lt;path&gt; +&lt;src&gt;:&lt;dst&gt;</c>: imports a bundle's objects and points <c>&lt;dst&gt;</c> at its tip.</summary>
    public static GitSimResult Fetch(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var path = ctx.Args.ElementAtOrDefault(1);
        var refspec = ctx.Args.ElementAtOrDefault(2);
        if (path is null || refspec is null || !File.Exists(path))
            return GitSimResult.Fatal("could not read bundle");

        var bundle = TryReadBundle(path);
        if (bundle is null)
            return GitSimResult.Fatal("not a valid bundle");

        ImportBundle(wt.Repo.Objects, bundle);
        var dst = refspec.TrimStart('+').Split(':').ElementAtOrDefault(1);
        if (dst is not null)
            wt.Repo.Refs[dst] = bundle.Tip;
        return GitSimResult.Ok();
    }

    private static BundleDto BuildBundle(GitObjectStore store, string refName, string tip)
    {
        var commits = new List<BundleCommit>();
        var trees = new Dictionary<string, BundleTree>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sha in ReachableFrom(store, tip))
        {
            if (!store.TryGetCommit(sha, out var c))
                continue;
            commits.Add(new BundleCommit(sha, c.TreeSha, c.Parents.ToList(), ToDto(c.Author), ToDto(c.Committer), c.Message));
            CollectTree(store, c.TreeSha, trees, blobs);
        }

        return new BundleDto(refName, tip, commits, trees.Values.ToList(), blobs);
    }

    private static void CollectTree(GitObjectStore store, string treeSha, Dictionary<string, BundleTree> trees, Dictionary<string, string> blobs)
    {
        if (trees.ContainsKey(treeSha) || !store.TryGetTree(treeSha, out var tree))
            return;
        trees[treeSha] = new BundleTree(treeSha, tree.Entries.Select(e => new BundleEntry(e.Mode, e.Name, e.Sha, e.Kind)).ToList());
        foreach (var e in tree.Entries)
            if (e.Kind == GitObjectKind.Tree)
                CollectTree(store, e.Sha, trees, blobs);
            else if (store.TryGetBlob(e.Sha, out var blob))
                blobs[e.Sha] = Convert.ToBase64String(blob.Content);
    }

    private static void ImportBundle(GitObjectStore store, BundleDto bundle)
    {
        foreach (var (_, base64) in bundle.Blobs)
            store.PutBlob(new GitBlob(Convert.FromBase64String(base64)));
        foreach (var t in bundle.Trees)
            store.PutTree(new GitTree(t.Entries.Select(e => new GitTreeEntry(e.Mode, e.Name, e.Sha, e.Kind)).ToList()));
        foreach (var c in bundle.Commits)
            store.PutCommit(new GitCommit(c.Tree, c.Parents, FromDto(c.Author), FromDto(c.Committer), c.Message));
    }

    private static BundleDto? TryReadBundle(string path)
    {
        try { return JsonSerializer.Deserialize<BundleDto>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static BundlePerson ToDto(GitPerson p) => new(p.Name, p.Email, p.When.ToUnixTimeSeconds(), (int)p.When.Offset.TotalMinutes);
    private static GitPerson FromDto(BundlePerson p) => new(p.Name, p.Email, DateTimeOffset.FromUnixTimeSeconds(p.Unix).ToOffset(TimeSpan.FromMinutes(p.OffsetMinutes)));
}
