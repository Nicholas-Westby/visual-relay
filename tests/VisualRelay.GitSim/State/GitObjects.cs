using System.Security.Cryptography;
using System.Text;

namespace VisualRelay.GitSim.State;

/// <summary>The kind of a stored git object referenced by a tree entry.</summary>
internal enum GitObjectKind
{
    Blob,
    Tree,
    Commit,
}

/// <summary>File content addressed by content hash. Bytes, so binary is faithful.</summary>
internal sealed record GitBlob(byte[] Content);

/// <summary>
/// One entry in a tree: a git mode (<c>100644</c>, <c>100755</c>, <c>40000</c> …),
/// the entry name (single path segment), the referenced object sha, and its kind.
/// </summary>
internal sealed record GitTreeEntry(string Mode, string Name, string Sha, GitObjectKind Kind);

/// <summary>A directory listing: entries sorted by name (git orders trees by name).</summary>
internal sealed record GitTree(IReadOnlyList<GitTreeEntry> Entries);

/// <summary>An author/committer identity plus the moment it was stamped.</summary>
internal sealed record GitPerson(string Name, string Email, DateTimeOffset When);

/// <summary>
/// A commit: root tree, ordered parent shas (0 = root commit, &gt;1 = merge),
/// author + committer identities, and the full message (subject + body).
/// </summary>
internal sealed record GitCommit(
    string TreeSha,
    IReadOnlyList<string> Parents,
    GitPerson Author,
    GitPerson Committer,
    string Message);

/// <summary>
/// Content-addressed store shared by every worktree of one repository. Objects are
/// keyed by a 40-hex SHA-1 over a canonical <c>&lt;kind&gt;\0&lt;payload&gt;</c>
/// serialization — not byte-identical to real git (trees/commits use a private
/// encoding) but stable, collision-safe, and shaped exactly like a git oid, which
/// is all any consumer relies on (shas are opaque tokens to them).
/// </summary>
internal sealed class GitObjectStore
{
    private readonly Dictionary<string, GitBlob> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GitTree> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GitCommit> _commits = new(StringComparer.Ordinal);

    public string PutBlob(GitBlob blob)
    {
        var sha = HashBlob(blob.Content);
        _blobs[sha] = blob;
        return sha;
    }

    public string PutTree(GitTree tree)
    {
        var sha = HashTree(tree);
        _trees[sha] = tree;
        return sha;
    }

    public string PutCommit(GitCommit commit)
    {
        var sha = HashCommit(commit);
        _commits[sha] = commit;
        return sha;
    }

    public bool TryGetBlob(string sha, out GitBlob blob) => _blobs.TryGetValue(sha, out blob!);
    public bool TryGetTree(string sha, out GitTree tree) => _trees.TryGetValue(sha, out tree!);
    public bool TryGetCommit(string sha, out GitCommit commit) => _commits.TryGetValue(sha, out commit!);

    public bool IsCommit(string sha) => _commits.ContainsKey(sha);
    public bool IsTree(string sha) => _trees.ContainsKey(sha);
    public bool Contains(string sha) =>
        _blobs.ContainsKey(sha) || _trees.ContainsKey(sha) || _commits.ContainsKey(sha);

    /// <summary>Copies any objects the <paramref name="other"/> store holds that this one lacks (bundle import).</summary>
    public void ImportFrom(GitObjectStore other)
    {
        foreach (var (sha, blob) in other._blobs) _blobs.TryAdd(sha, blob);
        foreach (var (sha, tree) in other._trees) _trees.TryAdd(sha, tree);
        foreach (var (sha, commit) in other._commits) _commits.TryAdd(sha, commit);
    }

    private static string HashBlob(byte[] content)
    {
        var payload = new byte[6 + content.Length];
        Encoding.ASCII.GetBytes("blob\0").CopyTo(payload, 0);
        content.CopyTo(payload, 5);
        return Hex(SHA1.HashData(payload));
    }

    private static string HashTree(GitTree tree)
    {
        var sb = new StringBuilder("tree\0");
        foreach (var e in tree.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            sb.Append(e.Mode).Append(' ').Append(e.Kind).Append(' ').Append(e.Sha).Append(' ').Append(e.Name).Append('\n');
        return Hex(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string HashCommit(GitCommit c)
    {
        var sb = new StringBuilder("commit\0");
        sb.Append("tree ").Append(c.TreeSha).Append('\n');
        foreach (var p in c.Parents) sb.Append("parent ").Append(p).Append('\n');
        sb.Append("author ").Append(Stamp(c.Author)).Append('\n');
        sb.Append("committer ").Append(Stamp(c.Committer)).Append('\n');
        sb.Append('\n').Append(c.Message);
        return Hex(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Stamp(GitPerson p) =>
        $"{p.Name} <{p.Email}> {p.When.ToUnixTimeSeconds()} {p.When.Offset.ToString(@"\+hhmm")}";

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
