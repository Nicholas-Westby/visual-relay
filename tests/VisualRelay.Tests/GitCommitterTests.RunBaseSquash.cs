using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for the in-run agent self-commit squash. When the swival
/// agent runs `git commit` itself mid-run (authorized via RELAY_COMMIT_TOKEN),
/// it lands a BARE provenance-less commit. The Commit stage must squash any
/// such commits made since run-start into the single sealed commit, so a task
/// is always exactly one sealed commit whose parent is the run-base.
/// </summary>
public sealed partial class GitCommitterTests
{
    // (b) One agent self-commit + further working-tree edits → one sealed commit.
    [Fact]
    public async Task CommitAsync_WithRunBase_SquashesAgentSelfCommitIntoOneSealedCommit()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Agent self-commits the bulk implementation mid-run (bare, no trailers).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "implemented");
        File.WriteAllText(Path.Combine(repo.Root, "src", "feature.cs"), "new feature");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip\"");

        // A further working-tree edit after the self-commit, not yet committed.
        File.WriteAllText(Path.Combine(repo.Root, "src", "extra.cs"), "extra");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs", "src/feature.cs", "src/extra.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");

        // Exactly ONE new commit on top of run-base: no bare commit remains.
        var countSinceBase = RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim();
        Assert.Equal("1", countSinceBase);

        // That single commit's parent is the run-base.
        var parent = RunGit(repo.Root, "rev-parse HEAD^").Trim();
        Assert.Equal(runBase, parent);

        // It is the sealed commit, carrying both trailers.
        var fullMessage = RunGit(repo.Root, "log -1 --pretty=%B");
        Assert.Contains("Task: my-task", fullMessage);
        Assert.Contains("Relay-Seal: abc123", fullMessage);

        // Nothing is lost: the full task diff is present in the committed tree.
        Assert.Equal("implemented", RunGit(repo.Root, "show HEAD:src/app.cs"));
        Assert.Equal("new feature", RunGit(repo.Root, "show HEAD:src/feature.cs"));
        Assert.Equal("extra", RunGit(repo.Root, "show HEAD:src/extra.cs"));

        // Working tree is clean (everything committed).
        Assert.Equal(string.Empty, RunGit(repo.Root, "status --porcelain").Trim());
    }

    // (b, plural) Several agent self-commits → all squashed into one sealed commit.
    [Fact]
    public async Task CommitAsync_WithRunBase_SquashesMultipleAgentSelfCommits()
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
        File.WriteAllText(Path.Combine(repo.Root, "src", "three.cs"), "3");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip 3\"");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "seal999",
            ["feat: build three things"],
            ["src/one.cs", "src/two.cs", "src/three.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());
        Assert.Equal(runBase, RunGit(repo.Root, "rev-parse HEAD^").Trim());
        Assert.Contains("Relay-Seal: seal999", RunGit(repo.Root, "log -1 --pretty=%B"));
        Assert.Equal("1", RunGit(repo.Root, "show HEAD:src/one.cs"));
        Assert.Equal("2", RunGit(repo.Root, "show HEAD:src/two.cs"));
        Assert.Equal("3", RunGit(repo.Root, "show HEAD:src/three.cs"));
    }

    // (a) No agent self-commit → unchanged: a single sealed commit on run-base.
    [Fact]
    public async Task CommitAsync_WithRunBase_NoSelfCommit_YieldsOneSealedCommit()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Agent left only working-tree changes (no self-commit).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());
        Assert.Equal(runBase, RunGit(repo.Root, "rev-parse HEAD^").Trim());
        var fullMessage = RunGit(repo.Root, "log -1 --pretty=%B");
        Assert.Contains("Task: my-task", fullMessage);
        Assert.Contains("Relay-Seal: abc123", fullMessage);
        Assert.Equal("updated", RunGit(repo.Root, "show HEAD:src/app.cs"));
    }

    // (c) Run-base several commits back (pre-existing history) is preserved;
    //     only the in-run agent commit is squashed, earlier history untouched.
    [Fact]
    public async Task CommitAsync_WithRunBase_PreservesEarlierHistory()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "v1");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "v2");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"chore: second\"");
        // Run starts here.
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();
        var rootCommit = RunGit(repo.Root, "rev-list --max-parents=0 HEAD").Trim();

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "v3");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"agent wip\"");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: ship v3"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        // One sealed commit on top of run-base.
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());
        Assert.Equal(runBase, RunGit(repo.Root, "rev-parse HEAD^").Trim());
        // Earlier history is intact: root commit and run-base still reachable.
        Assert.Equal("3", RunGit(repo.Root, "rev-list --count HEAD").Trim());
        Assert.Equal(rootCommit, RunGit(repo.Root, "rev-list --max-parents=0 HEAD").Trim());
        Assert.Equal("v3", RunGit(repo.Root, "show HEAD:src/app.cs"));
    }

    // The deference path: the squash MUST keep the candidate-retry loop and a
    // target repo's commit-msg hook intact. With an agent self-commit present
    // AND a hook rejecting the first candidate, the second candidate must win,
    // still as a single sealed commit on the run-base.
    [Fact]
    public async Task CommitAsync_WithRunBase_HonoursCommitMsgHookAfterSquash()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "base");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        var runBase = RunGit(repo.Root, "rev-parse HEAD").Trim();

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "implemented");
        RunGit(repo.Root, "add -A");
        RunGit(repo.Root, "commit -m \"wip\"");

        InstallRejectingCommitMsgHook(repo.Root, "\\.cs");

        var candidates = new[] { "fix(src): update app.cs logic", "fix: correct logic" };
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {runBase}..HEAD").Trim());
        Assert.Equal("fix: correct logic", RunGit(repo.Root, "log -1 --pretty=%s").Trim());
        Assert.Equal(runBase, RunGit(repo.Root, "rev-parse HEAD^").Trim());
    }
}
