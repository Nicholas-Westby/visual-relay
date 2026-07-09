namespace VisualRelay.GitSim.State;

/// <summary>
/// A stashed change set captured by <c>stash push -u</c>: the message, the file
/// contents that differed from HEAD (tracked edits + untracked, since <c>-u</c>),
/// and the tracked paths that were deleted. <c>stash apply</c> re-lays these down.
/// </summary>
internal sealed record StashEntry(
    string Message,
    string Branch,
    IReadOnlyDictionary<string, byte[]> Contents,
    IReadOnlySet<string> Deletions);

/// <summary>
/// The per-root half of a repository: its own HEAD and index (a linked worktree
/// shares the source's <see cref="GitRepository"/> object store and refs but owns
/// these), a working directory on the real filesystem, any <c>GIT_INDEX_FILE</c>
/// overrides, the stash stack, and cherry-pick sequencer state.
/// </summary>
internal sealed class Worktree(string root, GitRepository repo, GitHead head)
{
    public string Root { get; } = root;
    public GitRepository Repo { get; } = repo;
    public GitHead Head { get; } = head;
    public GitIndex Index { get; } = new();

    /// <summary>Per-path in-memory indexes addressed by a <c>GIT_INDEX_FILE</c> value.</summary>
    private readonly Dictionary<string, GitIndex> _indexOverrides = new(StringComparer.Ordinal);

    public List<StashEntry> Stashes { get; } = [];
    public bool CherryPickInProgress { get; set; }

    /// <summary>
    /// The index a command should act on: the <c>GIT_INDEX_FILE</c> override when the
    /// environment names one (created empty on first touch, as git would), else the
    /// worktree's own index.
    /// </summary>
    public GitIndex IndexFor(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is not null
            && environment.TryGetValue("GIT_INDEX_FILE", out var path)
            && !string.IsNullOrEmpty(path))
        {
            if (!_indexOverrides.TryGetValue(path, out var index))
            {
                index = new GitIndex();
                _indexOverrides[path] = index;
            }

            return index;
        }

        return Index;
    }

    /// <summary>Resolves HEAD to a commit sha, or null when HEAD is unborn/dangling.</summary>
    public string? ResolveHead()
    {
        if (Head.IsDetached)
            return Head.DetachedSha;
        return Head.SymbolicRef is not null && Repo.Refs.TryGetValue(Head.SymbolicRef, out var sha)
            ? sha
            : null;
    }

    /// <summary>Moves HEAD to <paramref name="sha"/>: updates the branch ref when attached, else the detached pointer.</summary>
    public void MoveHeadTo(string sha)
    {
        if (Head.IsDetached)
            Head.Detach(sha);
        else if (Head.SymbolicRef is not null)
            Repo.Refs[Head.SymbolicRef] = sha;
    }
}
