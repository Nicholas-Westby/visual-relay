using System.Collections.Concurrent;

namespace VisualRelay.GitSim.State;

/// <summary>
/// Process-wide, thread-safe map of repository root → <see cref="Worktree"/>. xUnit
/// runs test collections in parallel, so this must be concurrent; tests keep each
/// other isolated by using distinct (GUID) root paths, so no cross-test sharing
/// occurs despite the shared registry. Roots are normalized to a canonical
/// absolute form so the same repo is found regardless of trailing separators.
/// </summary>
internal static class GitSimRegistry
{
    private static readonly ConcurrentDictionary<string, Worktree> Worktrees =
        new(StringComparer.Ordinal);

    public static string Normalize(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>Registers (or re-initializes) a primary repository at <paramref name="root"/> on <paramref name="branch"/>.</summary>
    public static Worktree Init(string root, string branch)
    {
        var key = Normalize(root);
        return Worktrees.AddOrUpdate(
            key,
            _ =>
            {
                var repo = new GitRepository { DefaultBranch = branch };
                return new Worktree(key, repo, GitHead.Symbolic($"refs/heads/{branch}"));
            },
            (_, existing) => existing); // re-init is a no-op on an existing repo
    }

    /// <summary>Registers a linked worktree at <paramref name="root"/> sharing <paramref name="repo"/>, detached at <paramref name="sha"/>.</summary>
    public static Worktree AddLinked(string root, GitRepository repo, string? sha)
    {
        var key = Normalize(root);
        var head = sha is null ? GitHead.Symbolic($"refs/heads/{repo.DefaultBranch}") : GitHead.Detached(sha);
        var worktree = new Worktree(key, repo, head);
        Worktrees[key] = worktree;
        return worktree;
    }

    public static void Remove(string root) => Worktrees.TryRemove(Normalize(root), out _);

    public static bool TryGet(string root, out Worktree worktree) =>
        Worktrees.TryGetValue(Normalize(root), out worktree!);

    /// <summary>The worktree at <paramref name="root"/>, or null when it is not a registered repo.
    /// Walks up parent directories so that <c>rev-parse --show-toplevel</c> works from
    /// any subdirectory inside a registered repository.</summary>
    public static Worktree? Find(string root)
    {
        var normalized = Normalize(root);
        // Direct match first (common case).
        if (Worktrees.TryGetValue(normalized, out var direct))
            return direct;
        // Walk up parent directories to find the nearest ancestor repo.
        var dir = normalized;
        while (dir.Length > 0)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent.Length >= dir.Length)
                break;
            dir = parent;
            if (Worktrees.TryGetValue(dir, out var ancestor))
                return ancestor;
        }
        return null;
    }
}
