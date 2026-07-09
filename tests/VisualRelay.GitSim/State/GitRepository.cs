namespace VisualRelay.GitSim.State;

/// <summary>One staged path: git mode, blob sha, and merge stage (0 = merged).</summary>
internal sealed record IndexEntry(string Mode, string Sha, int Stage = 0);

/// <summary>
/// The staging area — a flat map of repo-relative path → entry, ordered so
/// enumeration is deterministic. Unmerged paths carry stage 1/2/3 entries under
/// the same path; <see cref="Stage0"/> filters to the merged view.
/// </summary>
internal sealed class GitIndex
{
    // Keyed by "path\0stage" so conflict stages coexist under one path.
    private readonly SortedDictionary<string, IndexEntry> _entries = new(StringComparer.Ordinal);

    public void Set(string path, IndexEntry entry) => _entries[$"{path}\0{entry.Stage}"] = entry;

    public void Remove(string path)
    {
        foreach (var stage in new[] { 0, 1, 2, 3 })
            _entries.Remove($"{path}\0{stage}");
    }

    public void Clear() => _entries.Clear();

    public bool TryGet(string path, out IndexEntry entry) => _entries.TryGetValue($"{path}\0{0}", out entry!);

    public IReadOnlyList<(string Path, IndexEntry Entry)> Stage0 =>
        _entries.Where(kv => kv.Value.Stage == 0)
            .Select(kv => (Path: kv.Key.Split('\0')[0], Entry: kv.Value))
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<(string Path, IndexEntry Entry)> Unmerged =>
        _entries.Where(kv => kv.Value.Stage != 0)
            .Select(kv => (Path: kv.Key.Split('\0')[0], Entry: kv.Value))
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .ThenBy(p => p.Entry.Stage)
            .ToList();

    public bool HasUnmerged => _entries.Values.Any(e => e.Stage != 0);

    public GitIndex Clone()
    {
        var copy = new GitIndex();
        foreach (var (key, value) in _entries)
            copy._entries[key] = value;
        return copy;
    }
}

/// <summary>
/// The tip pointer. Either symbolic (points at a branch ref, born or unborn) or
/// detached (points straight at a commit sha).
/// </summary>
internal sealed class GitHead
{
    public bool IsDetached { get; private set; }
    public string? SymbolicRef { get; private set; }
    public string? DetachedSha { get; private set; }

    public static GitHead Symbolic(string branchRef) => new() { SymbolicRef = branchRef };
    public static GitHead Detached(string sha) => new() { IsDetached = true, DetachedSha = sha };

    public void PointAt(string branchRef)
    {
        IsDetached = false;
        SymbolicRef = branchRef;
        DetachedSha = null;
    }

    public void Detach(string sha)
    {
        IsDetached = true;
        DetachedSha = sha;
        SymbolicRef = null;
    }
}

/// <summary>
/// The shared half of a repository: object store, refs (branches/tags/arbitrary),
/// config, and a monotonic clock giving un-dated commits deterministic, increasing
/// timestamps. Every linked worktree points at the same instance.
/// </summary>
internal sealed class GitRepository
{
    public GitObjectStore Objects { get; } = new();

    /// <summary>Full ref name (e.g. <c>refs/heads/main</c>) → commit sha.</summary>
    public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

    public string DefaultBranch { get; set; } = "main";
    public string? HooksPath { get; set; }
    public GitPerson Identity { get; set; } =
        new("VisualRelay Test", "test@example.test", DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

    private long _clock = 1_600_000_000;
    public DateTimeOffset NextTimestamp() => DateTimeOffset.FromUnixTimeSeconds(System.Threading.Interlocked.Increment(ref _clock));
}
