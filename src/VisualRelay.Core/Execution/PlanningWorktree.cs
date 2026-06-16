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
    private static string GetTempRoot(string repoRoot, string runId)
    {
        var repoHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repoRoot)))
        )[..12].ToLowerInvariant();
        return Path.Combine(
            Path.GetTempPath(),
            "visual-relay",
            "wt",
            repoHash,
            runId);
    }

    /// <summary>
    /// Creates a detached git worktree for the task under the per-run temp
    /// root, checked out at HEAD. Throws on failure.
    /// </summary>
    public static async Task<string> CreateAsync(string repoRoot, string taskId, string runId, CancellationToken ct, IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var worktreePath = Path.Combine(GetTempRoot(repoRoot, runId), taskId);
        if (Directory.Exists(worktreePath))
            Directory.Delete(worktreePath, recursive: true);

        await RunGitAsync(gi, repoRoot,
            ["worktree", "add", "--detach", "--quiet", worktreePath, "HEAD"],
            ct);

        return worktreePath;
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
    /// </summary>
    public static async Task PruneLeftoversAsync(string repoRoot, string runId, CancellationToken ct, IGitInvoker? gitInvoker = null)
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
        var repoHashDir = Path.GetDirectoryName(GetTempRoot(repoRoot, runId));
        if (repoHashDir is not null && Directory.Exists(repoHashDir))
        {
            foreach (var runDir in Directory.GetDirectories(repoHashDir))
            {
                try { Directory.Delete(runDir, recursive: true); }
                catch { /* concurrency-safe */ }
            }
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
