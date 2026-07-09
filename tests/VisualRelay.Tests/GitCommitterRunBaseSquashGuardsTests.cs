using VisualRelay.Core.Execution;
using VisualRelay.GitSim;
using static VisualRelay.Tests.GitCommitterGitSimSetup;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Data-loss regression tests for the in-run squash. The plain squash
/// (GitCommitterRunBaseSquashTests) proves the happy path; these prove the
/// two adversarial cases an earlier review found could destroy committed work:
/// (1) a stale run-base whose <c>runBase..HEAD</c> range crosses ANOTHER task's
///     SEALED commit — the squash must NOT reset across it, or that sealed commit
///     (its work + its provenance) is folded into the wrong seal and lost;
/// (2) every candidate message rejected by a target-repo hook AFTER the soft-reset
///     already rewound HEAD — HEAD must be restored to its pre-squash value so the
///     agent's self-commit is reinstated, not silently dropped on the next reset.
/// </summary>
public sealed class GitCommitterRunBaseSquashGuardsTests
{
    /// <summary>
    /// Reads a path's content at a revision. GitSim has no <c>show &lt;rev&gt;:&lt;path&gt;</c>
    /// (not a modeled command); <c>checkout &lt;rev&gt; -- &lt;path&gt;</c> materializes the
    /// same blob onto the real working tree, which is then read directly.
    /// </summary>
    private static async Task<string> ShowAsync(GitSimEngine sim, string root, string rev, string relPath)
    {
        await sim.Git(root, "checkout", rev, "--", relPath);
        return File.ReadAllText(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    // FIX 1 (CRITICAL): runBase..HEAD contains a SEALED commit (another task's
    // seal). The squash MUST be skipped — no reset — so the sealed commit and its
    // provenance survive. Better a cosmetic double-commit than a destroyed seal.
    [Fact]
    public async Task CommitAsync_WithRunBase_WhenRangeContainsSealedCommit_SkipsSquashAndPreservesSeal()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        // A bare agent self-commit (could legitimately be squashed) ...
        Write(repo, "src/early.cs", "early");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip early");

        // ... then ANOTHER task's SEALED commit lands on top (carries Relay-Seal:).
        // This is the intervening committed work a stale run-base must never cross.
        Write(repo, "src/other.cs", "other-task");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m",
            "feat(other): another task's sealed work\n\nTask: other-task\nRelay-Seal: deadbeefcafe\n");
        var sealedSha = sim.Head(repo.Root)!;

        // ... then a further bare self-commit on top of the seal.
        Write(repo, "src/late.cs", "late");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip late");
        var headBeforeCommit = sim.Head(repo.Root)!;

        // A working-tree edit for the (stale-run-base) task we are now sealing.
        Write(repo, "src/mine.cs", "mine");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "mytaskseal",
            ["feat: my task"], ["src/mine.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, sim, runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");

        // The squash was SKIPPED: HEAD's parent is the pre-existing tip
        // (headBeforeCommit), NOT the stale run-base. The reset never ran.
        Assert.Equal(headBeforeCommit, sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Parents[0]);

        // The sealed commit is still reachable, byte-for-byte intact.
        var (isAncestorExit, _) = await sim.Git(repo.Root, "merge-base", "--is-ancestor", sealedSha, "HEAD");
        Assert.Equal(0, isAncestorExit);
        Assert.Contains("Relay-Seal: deadbeefcafe", sim.CommitInfo(repo.Root, sealedSha)!.Message);
        Assert.Equal("other-task", await ShowAsync(sim, repo.Root, sealedSha, "src/other.cs"));

        // The other task's content survives in HEAD's history (never rewound away).
        Assert.Equal("other-task", await ShowAsync(sim, repo.Root, "HEAD", "src/other.cs"));
    }

    // FIX 1 control: a range of ONLY bare self-commits (no Relay-Seal:) still
    // squashes — the guard must not over-fire and block the legitimate case.
    [Fact]
    public async Task CommitAsync_WithRunBase_WhenRangeIsOnlyBareCommits_SquashesNormally()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        Write(repo, "src/one.cs", "1");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip 1");
        Write(repo, "src/two.cs", "2");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip 2");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "seal999",
            ["feat: build two things"], ["src/one.cs", "src/two.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, sim, runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        // Squash happened: exactly one sealed commit parented on the run-base.
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));
        var head = sim.Head(repo.Root)!;
        Assert.Equal(runBase, sim.CommitInfo(repo.Root, head)!.Parents[0]);
        Assert.Contains("Relay-Seal: seal999", sim.CommitInfo(repo.Root, head)!.Message);
    }

    // FIX 2 (HIGH): the soft-reset succeeds, then EVERY candidate is rejected by a
    // commit-msg hook. CommitAsync must return Failed AND restore HEAD to its
    // pre-squash value so the agent's self-commit (and its work) is reinstated —
    // otherwise the next worktree reset discards the staged delta and it is lost.
    [Fact]
    public async Task CommitAsync_WithRunBase_WhenAllCandidatesRejectedAfterSquash_RestoresOrigHead()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        // Agent self-commits the implementation mid-run (bare).
        Write(repo, "src/app.cs", "implemented");
        Write(repo, "src/feature.cs", "new feature");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip");
        var origHead = sim.Head(repo.Root)!;

        // Target repo hook rejects EVERY candidate message.
        sim.PreCommitHook = _ => GitSimHookVerdict.Reject("hook: all commits rejected");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget", "fix: alternative"], ["src/app.cs", "src/feature.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, sim, runBaseSha: runBase);

        // The commit failed (hook won) ...
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error);

        // ... but HEAD was rolled back to the pre-squash tip: the agent's
        // self-commit is BACK, nothing lost.
        Assert.Equal(origHead, sim.Head(repo.Root)!);
        // The committed tree still carries the agent's work.
        Assert.Equal("implemented", await ShowAsync(sim, repo.Root, "HEAD", "src/app.cs"));
        Assert.Equal("new feature", await ShowAsync(sim, repo.Root, "HEAD", "src/feature.cs"));
        // And run-base did NOT become HEAD (would mean the rewind stuck).
        Assert.NotEqual(runBase, sim.Head(repo.Root)!);
    }

    // FIX 3 (MEDIUM): content that lives only in the rewound COMMITTED tree (here a
    // file added by an early self-commit, never present in the final working tree
    // because a later self-commit git-rm'd it... no — simpler: a file the agent
    // committed and then deleted from the WORKING TREE but NOT from the index path)
    // must not be dropped. We model the index-only case: a tracked file added in an
    // in-run commit whose working-tree copy is removed before the seal. It must
    // still land in the sealed commit (staged from the pre-reset tree).
    [Fact]
    public async Task CommitAsync_WithRunBase_PreservesCommittedOnlyContentAcrossSquash()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        // Agent commits a new generated file mid-run (it is in the committed tree).
        Write(repo, "src/app.cs", "implemented");
        Write(repo, "src/generated.cs", "gen");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip");

        // The agent then removes the file from the WORKING TREE only (e.g. a tool
        // cleaned a temp build artifact) WITHOUT staging the deletion — so it lives
        // only in the committed tree at squash time. With the run-base as the seal
        // parent, working-tree-only staging would drop it; staging from the
        // pre-reset tree keeps it.
        File.Delete(Path.Combine(repo.Root, "src", "generated.cs"));

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, sim, runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));
        // The committed-only file survives the squash in the sealed commit.
        Assert.Equal("gen", await ShowAsync(sim, repo.Root, "HEAD", "src/generated.cs"));
        Assert.Equal("implemented", await ShowAsync(sim, repo.Root, "HEAD", "src/app.cs"));
    }
}
