using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Data-loss regression tests for the in-run squash. The plain squash
/// (GitCommitterTests.RunBaseSquash.cs) proves the happy path; these prove the
/// two adversarial cases an earlier review found could destroy committed work:
/// (1) a stale run-base whose <c>runBase..HEAD</c> range crosses ANOTHER task's
///     SEALED commit — the squash must NOT reset across it, or that sealed commit
///     (its work + its provenance) is folded into the wrong seal and lost;
/// (2) every candidate message rejected by a target-repo hook AFTER the soft-reset
///     already rewound HEAD — HEAD must be restored to its pre-squash value so the
///     agent's self-commit is reinstated, not silently dropped on the next reset.
/// </summary>
public sealed partial class GitCommitterTests
{
    // FIX 1 (CRITICAL): runBase..HEAD contains a SEALED commit (another task's
    // seal). The squash MUST be skipped — no reset — so the sealed commit and its
    // provenance survive. Better a cosmetic double-commit than a destroyed seal.
    [Fact]
    public async Task CommitAsync_WithRunBase_WhenRangeContainsSealedCommit_SkipsSquashAndPreservesSeal()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // A bare agent self-commit (could legitimately be squashed) ...
        File.WriteAllText(Path.Combine(repo.Root, "src", "early.cs"), "early");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip early\"");

        // ... then ANOTHER task's SEALED commit lands on top (carries Relay-Seal:).
        // This is the intervening committed work a stale run-base must never cross.
        File.WriteAllText(Path.Combine(repo.Root, "src", "other.cs"), "other-task");
        RunGit(repo.Root, "add -A");
        var sealMsgFile = Path.Combine(repo.Root, "seal-msg.txt");
        File.WriteAllText(sealMsgFile,
            "feat(other): another task's sealed work\n\nTask: other-task\nRelay-Seal: deadbeefcafe\n");
        RunGit(repo.Root, $"commit -F '{sealMsgFile}'");
        File.Delete(sealMsgFile);
        var sealedSha = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // ... then a further bare self-commit on top of the seal.
        File.WriteAllText(Path.Combine(repo.Root, "src", "late.cs"), "late");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip late\"");
        var headBeforeCommit = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // A working-tree edit for the (stale-run-base) task we are now sealing.
        File.WriteAllText(Path.Combine(repo.Root, "src", "mine.cs"), "mine");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "mytaskseal",
            ["feat: my task"], ["src/mine.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");

        // The squash was SKIPPED: HEAD's parent is the pre-existing tip
        // (headBeforeCommit), NOT the stale run-base. The reset never ran.
        Assert.Equal(headBeforeCommit, RunGit(repo.Root, "rev-parse HEAD^").Trim());

        // The sealed commit is still reachable, byte-for-byte intact.
        var sealedReachable = RunGit(repo.Root, $"merge-base --is-ancestor {sealedSha} HEAD; echo $?").Trim();
        Assert.Equal("0", sealedReachable);
        Assert.Contains("Relay-Seal: deadbeefcafe", RunGit(repo.Root, $"log -1 --pretty=%B {sealedSha}"));
        Assert.Equal("other-task", RunGit(repo.Root, $"show {sealedSha}:src/other.cs"));

        // The other task's content survives in HEAD's history (never rewound away).
        Assert.Equal("other-task", RunGit(repo.Root, "show HEAD:src/other.cs"));
    }

    // FIX 1 control: a range of ONLY bare self-commits (no Relay-Seal:) still
    // squashes — the guard must not over-fire and block the legitimate case.
    [Fact]
    public async Task CommitAsync_WithRunBase_WhenRangeIsOnlyBareCommits_SquashesNormally()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        File.WriteAllText(Path.Combine(repo.Root, "src", "one.cs"), "1");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip 1\"");
        File.WriteAllText(Path.Combine(repo.Root, "src", "two.cs"), "2");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip 2\"");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "seal999",
            ["feat: build two things"], ["src/one.cs", "src/two.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        // Squash happened: exactly one sealed commit parented on the run-base.
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());
        Assert.Equal(runBase, RunGit(repo.Root, "rev-parse HEAD^").Trim());
        Assert.Contains("Relay-Seal: seal999", RunGit(repo.Root, "log -1 --pretty=%B"));
    }

    // FIX 2 (HIGH): the soft-reset succeeds, then EVERY candidate is rejected by a
    // commit-msg hook. CommitAsync must return Failed AND restore HEAD to its
    // pre-squash value so the agent's self-commit (and its work) is reinstated —
    // otherwise the next worktree reset discards the staged delta and it is lost.
    [Fact]
    public async Task CommitAsync_WithRunBase_WhenAllCandidatesRejectedAfterSquash_RestoresOrigHead()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Agent self-commits the implementation mid-run (bare).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "implemented");
        File.WriteAllText(Path.Combine(repo.Root, "src", "feature.cs"), "new feature");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip\"");
        var origHead = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Target repo hook rejects EVERY candidate message.
        InstallRejectAllCommitMsgHook(repo.Root);

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget", "fix: alternative"], ["src/app.cs", "src/feature.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        // The commit failed (hook won) ...
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error);

        // ... but HEAD was rolled back to the pre-squash tip: the agent's
        // self-commit is BACK, nothing lost.
        Assert.Equal(origHead, RunGit(repo.Root, "rev-parse HEAD").Trim());
        // The committed tree still carries the agent's work.
        Assert.Equal("implemented", RunGit(repo.Root, "show HEAD:src/app.cs"));
        Assert.Equal("new feature", RunGit(repo.Root, "show HEAD:src/feature.cs"));
        // And run-base did NOT become HEAD (would mean the rewind stuck).
        Assert.NotEqual(runBase, RunGit(repo.Root, "rev-parse HEAD").Trim());
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
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Agent commits a new generated file mid-run (it is in the committed tree).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "implemented");
        File.WriteAllText(Path.Combine(repo.Root, "src", "generated.cs"), "gen");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip\"");

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
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());
        // The committed-only file survives the squash in the sealed commit.
        Assert.Equal("gen", RunGit(repo.Root, "show HEAD:src/generated.cs"));
        Assert.Equal("implemented", RunGit(repo.Root, "show HEAD:src/app.cs"));
    }
}
