using System.Security.Cryptography;
using System.Text;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Git worktree management for parallel planning isolation.
/// Worktrees are created OUTSIDE the main repo under a temp directory,
/// namespaced by repo-root hash and run id so concurrent drains to the
/// same repo from different processes never collide.
/// </summary>
public static class PlanningWorktree
{
    /// <summary>
    /// Root directory for all ephemeral planning worktrees created by this
    /// process. Deleted on completion; pruned on next drain after crash.
    /// </summary>
    /// <remarks>
    /// Rewrite worktrees (<paramref name="isRewrite"/> = <c>true</c>) live under a
    /// DISJOINT top-level segment (<c>wt-rewrite</c> vs <c>wt</c>). A rewrite is
    /// explicitly allowed to run WHILE the queue drains, and the drain calls
    /// <see cref="PruneLeftoversAsync"/> at every planning phase — which deletes
    /// every leftover run dir under its repo-hash namespace. Sharing one namespace
    /// would let the drain's prune wipe a live rewrite worktree out from under the
    /// running swival process (and vice-versa). Separate namespaces make a prune
    /// physically unable to see the other kind.
    /// </remarks>
    private static string GetTempRoot(string repoRoot, string runId, bool isRewrite)
    {
        var repoHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repoRoot)))
        )[..12].ToLowerInvariant();
        return Path.Combine(
            Path.GetTempPath(),
            "visual-relay",
            isRewrite ? "wt-rewrite" : "wt",
            repoHash,
            runId);
    }

    /// <summary>
    /// Creates a detached git worktree for the task under the per-run temp
    /// root, checked out at HEAD. Throws on failure. When <c>isRewrite</c> is
    /// <c>true</c> the worktree lives in the rewrite-only namespace so a concurrent
    /// drain's <see cref="PruneLeftoversAsync"/> cannot delete it.
    /// </summary>
    public static async Task<string> CreateAsync(string repoRoot, string taskId, string runId, CancellationToken ct, IGitInvoker? gitInvoker = null, bool isRewrite = false)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var worktreePath = Path.Combine(GetTempRoot(repoRoot, runId, isRewrite), taskId);
        if (Directory.Exists(worktreePath))
            Directory.Delete(worktreePath, recursive: true);

        await RunGitAsync(gi, repoRoot,
            ["worktree", "add", "--detach", "--quiet", worktreePath, "HEAD"],
            ct);

        return worktreePath;
    }

    /// <summary>
    /// Copies the source repo's <c>.relay/config.json</c> into the freshly
    /// created worktree so the planning stages can load the per-repo config.
    /// </summary>
    /// <remarks>
    /// A detached-HEAD checkout contains only COMMITTED files, but
    /// <c>.relay/config.json</c> is normally git-ignored (a repo-level
    /// <c>.gitignore</c> with <c>.relay/</c>), so it is absent from the
    /// checkout and the planning config load would otherwise throw
    /// "<c>.relay/config.json not found</c>". This mirrors the verify
    /// worktree's "provide a needed git-ignored file the checkout lacks"
    /// pattern. Best-effort: when the source has no config (the already-handled
    /// no-config path) nothing is copied, and any I/O failure is swallowed so
    /// worktree setup is never aborted.
    /// </remarks>
    public static void CopyConfigIntoWorktree(string sourceRepoRoot, string worktreePath)
    {
        var sourceConfig = Path.Combine(sourceRepoRoot, ".relay", "config.json");
        if (!File.Exists(sourceConfig))
            return; // no source config → leave the existing no-config path intact

        try
        {
            var destConfig = Path.Combine(worktreePath, ".relay", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(destConfig)!);
            File.Copy(sourceConfig, destConfig, overwrite: true);
        }
        catch
        {
            // Best-effort: a failed config copy must NOT abort worktree creation.
        }
    }

    /// <summary>
    /// Copies the per-task artifact directory (<c>.relay/&lt;taskId&gt;/</c>)
    /// from <paramref name="worktreePath"/> into the main repo at
    /// <paramref name="mainRepoRoot"/>.
    /// </summary>
    public static void CopyArtifactsBack(string mainRepoRoot, string worktreePath, string taskId)
    {
        var srcDir = Path.Combine(worktreePath, ".relay", taskId);
        if (!Directory.Exists(srcDir))
            return;

        var dstDir = Path.Combine(mainRepoRoot, ".relay", taskId);
        // Purge any stale partial copy, then clone the entire subtree.
        if (Directory.Exists(dstDir))
            Directory.Delete(dstDir, recursive: true);

        Directory.CreateDirectory(Path.GetDirectoryName(dstDir)!);
        CopyDirectoryRecursive(srcDir, dstDir);
    }

    /// <summary>
    /// Removes the git worktree entry and the on-disk directory.
    /// Best-effort — never throws.
    /// </summary>
    public static async Task RemoveAsync(string repoRoot, string worktreePath, CancellationToken ct, IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        try
        {
            if (Directory.Exists(worktreePath))
                await RunGitAsync(gi, repoRoot, ["worktree", "remove", "--force", worktreePath], ct);
        }
        catch
        {
            // Best-effort: the next prune will clean it up.
        }
    }

    /// <summary>
    /// Prunes stale git worktree entries and deletes any leftover worktree
    /// directories from a prior crashed drain. Called at drain start.
    /// Cleans ALL leftover directories under the repo-hash namespace, not
    /// just the current runId, so crashes from prior drains are recovered.
    /// The sweep is scoped to the rewrite namespace when <c>isRewrite</c> is
    /// <c>true</c>: a planning-phase prune (<c>false</c>) therefore never touches a
    /// live rewrite worktree, and a rewrite-namespace prune never touches a live
    /// planning worktree.
    /// </summary>
    public static async Task PruneLeftoversAsync(string repoRoot, string runId, CancellationToken ct, IGitInvoker? gitInvoker = null, bool isRewrite = false)
    {
        var gi = gitInvoker ?? new GitInvoker();
        try
        {
            await RunGitAsync(gi, repoRoot, ["worktree", "prune"], ct);
        }
        catch
        {
            // Best-effort.
        }

        // Clean all run-id directories under the repo-hash namespace, not just
        // the current runId, to recover disk space from prior crashed drains.
        var repoHashDir = Path.GetDirectoryName(GetTempRoot(repoRoot, runId, isRewrite));
        if (repoHashDir is not null && Directory.Exists(repoHashDir))
        {
            foreach (var runDir in Directory.GetDirectories(repoHashDir))
            {
                try { Directory.Delete(runDir, recursive: true); }
                catch { /* concurrency-safe */ }
            }
        }
    }

    /// <summary>
    /// Crash-recovery for the rewrite namespace, SCOPED to a single
    /// <paramref name="taskId"/>: prunes stale git worktree admin entries and
    /// deletes only this task's leftover worktree directories (any runId) under
    /// <c>wt-rewrite/&lt;repoHash&gt;/</c>. Because a task can never be rewritten
    /// twice concurrently, deleting its own leftovers is safe and — unlike a
    /// blanket <see cref="PruneLeftoversAsync"/> over the rewrite namespace —
    /// cannot wipe a concurrent rewrite of a DIFFERENT task that shares the
    /// namespace. Best-effort: never throws.
    /// </summary>
    public static async Task PruneTaskLeftoversAsync(string repoRoot, string taskId, CancellationToken ct, IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        try
        {
            await RunGitAsync(gi, repoRoot, ["worktree", "prune"], ct);
        }
        catch
        {
            // Best-effort.
        }

        // Sample the repo-hash dir via a throwaway runId; only the parent matters.
        var repoHashDir = Path.GetDirectoryName(GetTempRoot(repoRoot, "_", isRewrite: true));
        if (repoHashDir is null || !Directory.Exists(repoHashDir))
            return;

        foreach (var runDir in Directory.GetDirectories(repoHashDir))
        {
            var taskLeaf = Path.Combine(runDir, taskId);
            if (!Directory.Exists(taskLeaf))
                continue;
            try { Directory.Delete(taskLeaf, recursive: true); }
            catch { /* concurrency-safe best-effort */ }
        }
    }

    private static async Task RunGitAsync(IGitInvoker gitInvoker, string repoRoot, string[] args, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await gitInvoker.RunAsync(repoRoot, args, ct);

                if (result.ExitCode == 0)
                    return;

                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException(
                        $"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Output.Trim()}");
                }

                var delay = attempt == 1 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // Process start failure — also retry.
                if (attempt == maxAttempts)
                    throw;
                var delay = attempt == 1 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException(
            $"git {string.Join(' ', args)} failed after {maxAttempts} attempts");
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
