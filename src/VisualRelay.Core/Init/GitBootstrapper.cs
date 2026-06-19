using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

/// <summary>
/// Ensures a target directory is a git repository with at least one commit, so
/// Visual Relay can create planning/verify worktrees (<c>git worktree add HEAD</c>)
/// and land sealed commits there. This is the git half of greenfield bootstrap:
/// pointing Visual Relay at an empty folder should "just work".
/// </summary>
public static class GitBootstrapper
{
    /// <summary>True when <paramref name="rootPath"/> is inside a git work tree.</summary>
    public static async Task<bool> IsRepositoryAsync(
        string rootPath, IGitInvoker? gitInvoker = null, CancellationToken cancellationToken = default)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var inside = await gi.RunAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        return inside.ExitCode == 0 && inside.Output.Trim().Equals("true", StringComparison.Ordinal);
    }

    /// <summary>
    /// Guarantees <paramref name="rootPath"/> is a git repository with a resolvable
    /// HEAD. Initializes a new repository and makes an initial commit when none
    /// exists; for an existing repository with an unborn HEAD, makes the initial
    /// commit only. Returns true when it created a brand-new repository.
    /// </summary>
    public static async Task<bool> EnsureRepositoryAsync(
        string rootPath, IGitInvoker? gitInvoker = null, CancellationToken cancellationToken = default)
    {
        var gi = gitInvoker ?? new GitInvoker();

        var alreadyRepo = await IsRepositoryAsync(rootPath, gi, cancellationToken);
        if (!alreadyRepo)
        {
            var init = await gi.RunAsync(rootPath, ["init"], cancellationToken);
            if (init.ExitCode != 0)
            {
                throw new InvalidOperationException($"git init failed (exit {init.ExitCode}): {init.Output.Trim()}");
            }
        }

        await EnsureInitialCommitAsync(rootPath, gi, cancellationToken);
        return !alreadyRepo;
    }

    /// <summary>
    /// Makes the initial commit when HEAD is unborn, so worktrees can be created.
    /// No-op once any commit exists — an established repo is never auto-committed to.
    /// </summary>
    private static async Task EnsureInitialCommitAsync(
        string rootPath, IGitInvoker gi, CancellationToken cancellationToken)
    {
        var head = await gi.RunAsync(rootPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        if (head.ExitCode == 0)
        {
            return; // HEAD already resolves
        }

        // Stage whatever is present (e.g. the .relay config the bootstrapper just
        // wrote) and seal an initial commit. --allow-empty guarantees a commit even
        // for a truly empty folder, so HEAD always resolves afterwards.
        await gi.RunAsync(rootPath, ["add", "-A"], cancellationToken);
        var commit = await gi.RunAsync(
            rootPath,
            ["commit", "--allow-empty", "-m", "chore: initialize repository (visual-relay bootstrap)"],
            cancellationToken);
        if (commit.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git initial commit failed (exit {commit.ExitCode}): {commit.Output.Trim()}");
        }
    }
}
