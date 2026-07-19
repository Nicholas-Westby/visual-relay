using VisualRelay.Core.Execution;
using VisualRelay.GitSim;
using static VisualRelay.Tests.GitCommitterGitSimSetup;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for the in-run agent self-commit squash. When the swival
/// agent runs `git commit` itself mid-run (authorized via RELAY_COMMIT_TOKEN),
/// it lands a BARE provenance-less commit. The Commit stage must squash any
/// such commits made since run-start into the single sealed commit, so a task
/// is always exactly one sealed commit whose parent is the run-base.
/// </summary>
public sealed class GitCommitterRunBaseSquashTests
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

    // (b) One agent self-commit + further working-tree edits → one sealed commit.
    [Fact]
    public async Task CommitAsync_WithRunBase_SquashesAgentSelfCommitIntoOneSealedCommit()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        // Agent self-commits the bulk implementation mid-run (bare, no trailers).
        Write(repo, "src/app.cs", "implemented");
        Write(repo, "src/feature.cs", "new feature");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip");

        // A further working-tree edit after the self-commit, not yet committed.
        Write(repo, "src/extra.cs", "extra");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs", "src/feature.cs", "src/extra.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, runBaseSha: runBase, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");

        // Exactly ONE new commit on top of run-base: no bare commit remains.
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));

        // That single commit's parent is the run-base, and it is the sealed
        // commit, carrying both trailers.
        var head = sim.Head(repo.Root)!;
        var headInfo = sim.CommitInfo(repo.Root, head)!;
        Assert.Equal(runBase, headInfo.Parents[0]);
        Assert.Contains("Task: my-task", headInfo.Message);
        Assert.Contains("Relay-Seal: abc123", headInfo.Message);

        // Nothing is lost: the full task diff is present in the committed tree.
        Assert.Equal("implemented", await ShowAsync(sim, repo.Root, "HEAD", "src/app.cs"));
        Assert.Equal("new feature", await ShowAsync(sim, repo.Root, "HEAD", "src/feature.cs"));
        Assert.Equal("extra", await ShowAsync(sim, repo.Root, "HEAD", "src/extra.cs"));

        // Working tree is clean (everything committed).
        Assert.Equal(string.Empty, (await sim.Git(repo.Root, "status", "--porcelain")).Output.Trim());
    }

    // (b, plural) Several agent self-commits → all squashed into one sealed commit.
    [Fact]
    public async Task CommitAsync_WithRunBase_SquashesMultipleAgentSelfCommits()
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
        Write(repo, "src/three.cs", "3");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip 3");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "seal999",
            ["feat: build three things"],
            ["src/one.cs", "src/two.cs", "src/three.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, runBaseSha: runBase, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));
        var head = sim.Head(repo.Root)!;
        Assert.Equal(runBase, sim.CommitInfo(repo.Root, head)!.Parents[0]);
        Assert.Contains("Relay-Seal: seal999", sim.CommitInfo(repo.Root, head)!.Message);
        Assert.Equal("1", await ShowAsync(sim, repo.Root, "HEAD", "src/one.cs"));
        Assert.Equal("2", await ShowAsync(sim, repo.Root, "HEAD", "src/two.cs"));
        Assert.Equal("3", await ShowAsync(sim, repo.Root, "HEAD", "src/three.cs"));
    }

    // (a) No agent self-commit → unchanged: a single sealed commit on run-base.
    [Fact]
    public async Task CommitAsync_WithRunBase_NoSelfCommit_YieldsOneSealedCommit()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        // Agent left only working-tree changes (no self-commit).
        Write(repo, "src/app.cs", "updated");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, runBaseSha: runBase, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));
        var head = sim.Head(repo.Root)!;
        var headInfo = sim.CommitInfo(repo.Root, head)!;
        Assert.Equal(runBase, headInfo.Parents[0]);
        Assert.Contains("Task: my-task", headInfo.Message);
        Assert.Contains("Relay-Seal: abc123", headInfo.Message);
        Assert.Equal("updated", await ShowAsync(sim, repo.Root, "HEAD", "src/app.cs"));
    }

    // (c) Run-base several commits back (pre-existing history) is preserved;
    //     only the in-run agent commit is squashed, earlier history untouched.
    [Fact]
    public async Task CommitAsync_WithRunBase_PreservesEarlierHistory()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "v1");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "v2");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "chore: second");
        // Run starts here.
        var runBase = sim.Head(repo.Root)!;
        var rootCommit = (await sim.Git(repo.Root, "rev-list", "HEAD")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];

        Write(repo, "src/app.cs", "v3");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "agent wip");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: ship v3"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, runBaseSha: runBase, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        // One sealed commit on top of run-base.
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));
        var head = sim.Head(repo.Root)!;
        Assert.Equal(runBase, sim.CommitInfo(repo.Root, head)!.Parents[0]);
        // Earlier history is intact: root commit and run-base still reachable.
        var reachable = (await sim.Git(repo.Root, "rev-list", "HEAD")).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, reachable.Length);
        Assert.Equal(rootCommit, reachable[^1]);
        Assert.Equal("v3", await ShowAsync(sim, repo.Root, "HEAD", "src/app.cs"));
    }

    // The deference path: the squash MUST keep the candidate-retry loop and a
    // target repo's commit-msg hook intact. With an agent self-commit present
    // AND a hook rejecting the first candidate, the second candidate must win,
    // still as a single sealed commit on the run-base.
    [Fact]
    public async Task CommitAsync_WithRunBase_HonoursCommitMsgHookAfterSquash()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "base");
        sim.Commit(repo.Root, "chore: seed");
        var runBase = sim.Head(repo.Root)!;

        Write(repo, "src/app.cs", "implemented");
        await sim.Git(repo.Root, "add", "-A");
        await sim.Git(repo.Root, "commit", "-m", "wip");

        sim.PreCommitHook = req =>
            req.Message.Split('\n')[0].Contains(".cs", StringComparison.Ordinal)
                ? GitSimHookVerdict.Reject("hook: subject matches rejected pattern")
                : GitSimHookVerdict.Accept;

        var candidates = new[] { "fix(src): update app.cs logic", "fix: correct logic" };
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, runBaseSha: runBase, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Single(sim.CommitsBetween(repo.Root, runBase, "HEAD"));
        var head = sim.Head(repo.Root)!;
        Assert.Equal("fix: correct logic", Subject(sim, repo, head));
        Assert.Equal(runBase, sim.CommitInfo(repo.Root, head)!.Parents[0]);
    }
}
