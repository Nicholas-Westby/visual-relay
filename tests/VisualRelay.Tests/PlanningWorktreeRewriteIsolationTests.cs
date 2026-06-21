using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A queue drain is explicitly allowed to run WHILE a "Rewrite with AI" runs
/// concurrently. The drain calls <see cref="PlanningWorktree.PruneLeftoversAsync"/>
/// at the start of every planning phase, which deletes ALL leftover run
/// directories under its temp namespace. If planning worktrees and rewrite
/// worktrees shared that namespace, the drain's prune would
/// <c>Directory.Delete</c> a LIVE rewrite worktree out from under the running
/// swival process.
///
/// The fix gives rewrite worktrees a DISJOINT namespace so a planning-phase
/// prune cannot see them (and a rewrite's own cleanup cannot see planning
/// worktrees). These tests use the real GitInvoker against a temp git repo,
/// mirroring <see cref="PlanningWorktreeConfigCopyTests"/>.
/// </summary>
public sealed class PlanningWorktreeRewriteIsolationTests
{
    private static void InitRepo(string root)
    {
        TestGit.Run(root, "init", "-q");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Repo\n");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-q", "-m", "seed");
    }

    // ───────────────────────────────────────────────────────────────────
    // A planning-phase prune (drain start) must NOT delete a live rewrite
    // worktree. PRE-FIX they shared the wt/<repoHash>/ namespace, so the
    // prune wiped the rewrite worktree mid-run.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task PlanningPrune_DoesNotDeleteLiveRewriteWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? rewriteWt = null;
        try
        {
            InitRepo(root);

            // A rewrite worktree is live (the concurrently-running rewrite).
            rewriteWt = await PlanningWorktree.CreateAsync(
                root, "rewrite-task", "rewrite-123", CancellationToken.None, isRewrite: true);
            Assert.True(Directory.Exists(rewriteWt));

            // A drain begins: planning prunes its own (non-rewrite) namespace.
            await PlanningWorktree.PruneLeftoversAsync(
                root, "20260101000000-plan-task", CancellationToken.None);

            Assert.True(Directory.Exists(rewriteWt),
                "a planning-phase prune must NOT delete a live rewrite worktree");
        }
        finally
        {
            if (rewriteWt is not null)
                await PlanningWorktree.RemoveAsync(root, rewriteWt, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // The reciprocal: a rewrite cleanup (which prunes/removes only its own
    // namespace) must NOT delete a live planning worktree.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RewriteCleanup_DoesNotDeleteLivePlanningWorktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-iso2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? planningWt = null;
        try
        {
            InitRepo(root);

            // A planning worktree is live (a normal drain in progress).
            planningWt = await PlanningWorktree.CreateAsync(
                root, "plan-task", "20260101000000-plan-task", CancellationToken.None);
            Assert.True(Directory.Exists(planningWt));

            // A rewrite finishes and prunes its OWN namespace.
            await PlanningWorktree.PruneLeftoversAsync(
                root, "rewrite-999", CancellationToken.None, isRewrite: true);

            Assert.True(Directory.Exists(planningWt),
                "a rewrite-namespace prune must NOT delete a live planning worktree");
        }
        finally
        {
            if (planningWt is not null)
                await PlanningWorktree.RemoveAsync(root, planningWt, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // Crash recovery for the rewrite namespace must be SCOPED to one task id:
    // it reclaims a prior crashed rewrite of THE SAME task (different runId)
    // but must NOT delete a CONCURRENT live rewrite of a DIFFERENT task that
    // shares the wt-rewrite/<repoHash>/ namespace.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task PruneTaskLeftovers_ReclaimsSameTaskCrash_ButSparesConcurrentSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-iso4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? sibling = null;
        try
        {
            InitRepo(root);

            // A crashed prior rewrite of "task-a" left a worktree behind.
            var stale = await PlanningWorktree.CreateAsync(
                root, "task-a", "rewrite-OLD", CancellationToken.None, isRewrite: true);
            // A DIFFERENT task is being rewritten concurrently right now.
            sibling = await PlanningWorktree.CreateAsync(
                root, "task-b", "rewrite-LIVE", CancellationToken.None, isRewrite: true);

            // Starting a fresh rewrite of "task-a" reclaims only task-a's leftovers.
            await PlanningWorktree.PruneTaskLeftoversAsync(root, "task-a", CancellationToken.None);

            Assert.False(Directory.Exists(stale),
                "a same-task crashed rewrite worktree must be reclaimed");
            Assert.True(Directory.Exists(sibling),
                "a concurrent rewrite of a DIFFERENT task must NOT be deleted");
        }
        finally
        {
            if (sibling is not null)
                await PlanningWorktree.RemoveAsync(root, sibling, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // A rewrite worktree lives under a DISTINCT top-level segment from the
    // planning worktrees, so the two namespaces can never collide.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RewriteAndPlanningWorktrees_LiveInDisjointNamespaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-iso3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? planningWt = null;
        string? rewriteWt = null;
        try
        {
            InitRepo(root);

            planningWt = await PlanningWorktree.CreateAsync(
                root, "task", "20260101000000-task", CancellationToken.None);
            rewriteWt = await PlanningWorktree.CreateAsync(
                root, "task", "rewrite-1", CancellationToken.None, isRewrite: true);

            // The repo-hash parent directories (…/<segment>/<repoHash>/) differ,
            // so a Directory.GetDirectories under one can never see the other.
            var planningRepoHashDir = Path.GetDirectoryName(Path.GetDirectoryName(planningWt));
            var rewriteRepoHashDir = Path.GetDirectoryName(Path.GetDirectoryName(rewriteWt));
            Assert.NotEqual(planningRepoHashDir, rewriteRepoHashDir);
        }
        finally
        {
            if (planningWt is not null)
                await PlanningWorktree.RemoveAsync(root, planningWt, CancellationToken.None);
            if (rewriteWt is not null)
                await PlanningWorktree.RemoveAsync(root, rewriteWt, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
