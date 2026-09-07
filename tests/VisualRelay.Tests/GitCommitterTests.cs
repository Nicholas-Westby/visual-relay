using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

/// <summary>
/// The commit family, migrated onto GitSim. This class holds the low-latency facts —
/// the happy-path candidate acceptance and the gitignored-manifest backstop — so its
/// solo run stays fast. The hook-rejection and retry-after-transient facts (which
/// exercise GitCommitter's real, unavoidable retry backoff and so cost seconds
/// regardless of the git backend) live in sibling classes that run as parallel
/// collections; all keep their original assertions, now against in-memory GitSim state
/// and, for hook rejection, via <c>GitSim.PreCommitHook</c>.
/// </summary>
public sealed class GitCommitterTests
{
    [Fact]
    public async Task CommitAsync_FirstCandidateAccepted_CommitsAndReturnsSha()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");

        var candidates = new[] { "feat: add widget", "docs: update readme" };
        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", candidates, ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.CommitSha));
        var info = sim.CommitInfo(repo.Root, result.CommitSha!)!;
        Assert.Equal("feat: add widget", info.Message.Split('\n')[0]);
        Assert.Contains("Task: my-task", info.Message, StringComparison.Ordinal);
        Assert.Contains("Relay-Seal: abc123", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAsync_WhenManifestContainsGitignoredPath_ReturnsExplicitPathNames()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, ".gitignore", "config.local.toml\n");
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        // Runtime artifact that exists on disk but is gitignored: the manifest must not claim it.
        Write(repo, "config.local.toml", "[runtime]\nkey = \"val\"");
        Write(repo, "src/app.cs", "updated");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["config.local.toml", "src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("manifest contains gitignored", result.Error, StringComparison.Ordinal);
        Assert.Contains("config.local.toml", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAsync_WhenTrackedRelayConfigIsModified_LeavesItOutOfTheCommit()
    {
        // A tracked .relay/config.json edited during the run (skip-tests prune,
        // a settings toggle) must not ride along in the sealed commit: the
        // tracked-changes add excludes .relay, so the edit stays in the tree.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, ".gitignore", ".relay/*\n!.relay/config.json\n");
        sim.Seed(repo.Root, ".relay/config.json", "{\"testCmd\":\"old\"}");
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, ".relay/config.json", "{\"testCmd\":\"new\"}");
        Write(repo, "src/app.cs", "updated");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        var changed = sim.FilesChangedInCommit(repo.Root, result.CommitSha!);
        Assert.Contains("src/app.cs", changed);
        Assert.DoesNotContain(".relay/config.json", changed);

        // The edit survives the commit as an unstaged working-tree change.
        var (_, status) = await sim.Git(repo.Root, "status", "--porcelain");
        Assert.Contains(" M .relay/config.json", status, StringComparison.Ordinal);
        Assert.Equal("{\"testCmd\":\"new\"}", File.ReadAllText(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task CommitAsync_WhenManifestListsARelayPath_NeverStagesIt()
    {
        // Stage 4 can name a .relay path (a repo that does not gitignore .relay
        // passes the ignored-path pre-check). The manifest add must still skip it.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");
        Write(repo, ".relay/my-task/manifest.txt", "src/app.cs\n");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"],
            [".relay/my-task/manifest.txt", "src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        var changed = sim.FilesChangedInCommit(repo.Root, result.CommitSha!);
        Assert.Contains("src/app.cs", changed);
        Assert.DoesNotContain(".relay/my-task/manifest.txt", changed);
    }

    [Fact]
    public async Task CommitAsync_WhenTheTasksDirIsGitignored_StillStagesTheRetiredTaskFile()
    {
        // A repo may keep its task files out of git. The retirement move must land
        // anyway: the same commit stages the DELETION of the original task file, so
        // without its archived replacement the task would vanish from history.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, ".gitignore", "llm-tasks/\n");
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");
        Write(repo, "src/app.cs", "updated");
        Write(repo, "llm-tasks/completed/DONE-my-task.md", "# done");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["src/app.cs"],
            ["llm-tasks/completed/DONE-my-task.md"],
            commitToken: null, preRunUntracked: null, tasksDir: "llm-tasks",
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        var changed = sim.FilesChangedInCommit(repo.Root, result.CommitSha!);
        Assert.Contains("llm-tasks/completed/DONE-my-task.md", changed);
        Assert.Contains("src/app.cs", changed);
    }
}
