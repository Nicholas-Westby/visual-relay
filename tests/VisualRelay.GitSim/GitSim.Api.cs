using System.Text;
using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

/// <summary>Commit metadata returned by <see cref="GitSim.CommitInfo"/> for test assertions.</summary>
public sealed record GitSimCommitInfo(
    string Sha,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommitterDate,
    IReadOnlyList<string> Parents);

/// <summary>
/// The seeding + inspection surface a migrated test uses instead of shelling out to
/// git: build up repo state (<see cref="InitRepo"/>, <see cref="Seed"/>,
/// <see cref="Commit"/>) and read it back (<see cref="Head"/>, <see cref="CommitInfo"/>,
/// …) — replacing <c>git init</c>/<c>git add</c>/<c>git commit</c>/<c>git log</c> in
/// test setup and assertions. Every method resolves the repo from the shared registry.
/// </summary>
public sealed partial class GitSim
{
    /// <summary>Registers a repository at <paramref name="root"/> on <paramref name="branch"/> (unborn HEAD).</summary>
    public string InitRepo(string root, string branch = "main")
    {
        GitSimRegistry.Init(root, branch);
        return GitSimRegistry.Normalize(root);
    }

    /// <summary>Writes <paramref name="content"/> to the real file at <paramref name="relPath"/> and stages it.</summary>
    public void Seed(string root, string relPath, string content)
    {
        var wt = Require(root);
        WorkingTree.WriteBytes(root, relPath, Encoding.UTF8.GetBytes(content));
        var sha = wt.Repo.Objects.PutBlob(new GitBlob(Encoding.UTF8.GetBytes(content)));
        wt.Index.Set(relPath.Replace('\\', '/'), new IndexEntry(WorkingTree.ModeOnDisk(root, relPath), sha));
    }

    /// <summary>Records a commit from the staged index, advancing HEAD, and returns its sha.</summary>
    public string Commit(
        string root,
        string message,
        (string Name, string Email)? author = null,
        (DateTimeOffset Author, DateTimeOffset Committer)? dates = null)
    {
        var wt = Require(root);
        var store = wt.Repo.Objects;
        var treeSha = TreeBuilder.BuildTreeFromIndex(store, wt.Index);
        var head = wt.ResolveHead();
        var when = wt.Repo.NextTimestamp();
        var authorPerson = new GitPerson(
            author?.Name ?? wt.Repo.Identity.Name,
            author?.Email ?? wt.Repo.Identity.Email,
            dates?.Author ?? when);
        var committerPerson = new GitPerson(
            author?.Name ?? wt.Repo.Identity.Name,
            author?.Email ?? wt.Repo.Identity.Email,
            dates?.Committer ?? when);
        var parents = head is null ? Array.Empty<string>() : [head];
        var sha = store.PutCommit(new GitCommit(treeSha, parents, authorPerson, committerPerson, message));
        wt.MoveHeadTo(sha);
        return sha;
    }

    /// <summary>The current HEAD commit sha, or null when HEAD is unborn.</summary>
    public string? Head(string root) => Require(root).ResolveHead();

    /// <summary>The tip of <c>refs/heads/&lt;name&gt;</c>, or null when the branch does not exist.</summary>
    public string? BranchTip(string root, string name) =>
        Require(root).Repo.Refs.GetValueOrDefault($"refs/heads/{name}");

    /// <summary>Commit shas reachable from <paramref name="b"/> but not <paramref name="a"/>, newest first.</summary>
    public IReadOnlyList<string> CommitsBetween(string root, string a, string b)
    {
        var wt = Require(root);
        var baseRev = GitSimCommands.ResolveRevision(wt, a);
        var tip = GitSimCommands.ResolveRevision(wt, b);
        return baseRev is null || tip is null ? [] : GitSimCommands.RangeCommits(wt, baseRev, tip);
    }

    /// <summary>Author/committer/message/dates for a commit, or null when it does not resolve.</summary>
    public GitSimCommitInfo? CommitInfo(string root, string sha)
    {
        var wt = Require(root);
        var resolved = GitSimCommands.ResolveRevision(wt, sha);
        if (resolved is null || !wt.Repo.Objects.TryGetCommit(resolved, out var c))
            return null;
        return new GitSimCommitInfo(
            resolved, c.Message, c.Author.Name, c.Author.Email, c.Author.When,
            c.Committer.Name, c.Committer.Email, c.Committer.When, c.Parents);
    }

    /// <summary>The file paths contained in a commit's tree, sorted.</summary>
    public IReadOnlyList<string> FilesInCommit(string root, string sha)
    {
        var wt = Require(root);
        var resolved = GitSimCommands.ResolveRevision(wt, sha);
        if (resolved is null || !wt.Repo.Objects.TryGetCommit(resolved, out var c))
            return [];
        return TreeBuilder.FlattenTree(wt.Repo.Objects, c.TreeSha).Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>The stage-0 paths currently in the index.</summary>
    public IReadOnlyList<string> StagedPaths(string root) =>
        Require(root).Index.Stage0.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>Whether <paramref name="rel"/> is ignored by the repo-root <c>.gitignore</c>.</summary>
    public bool IsIgnored(string root, string rel) => GitIgnore.Load(GitSimRegistry.Normalize(root)).IsIgnored(rel);

    /// <summary>Whether a ref exists — matched as a full ref name, or under <c>refs/heads</c>/<c>refs/tags</c>.</summary>
    public bool RefExists(string root, string name)
    {
        var refs = Require(root).Repo.Refs;
        return refs.ContainsKey(name)
            || refs.ContainsKey($"refs/heads/{name}")
            || refs.ContainsKey($"refs/tags/{name}");
    }

    private static Worktree Require(string root) =>
        GitSimRegistry.Find(root)
        ?? throw new InvalidOperationException($"GitSim: no repository registered at '{root}' (call InitRepo first)");
}
