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
            sim, CancellationToken.None);

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
        sim.Seed(repo.Root, ".gitignore", "swival.toml\n");
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        // Runtime artifact that exists on disk but is gitignored: the manifest must not claim it.
        Write(repo, "swival.toml", "[runtime]\nkey = \"val\"");
        Write(repo, "src/app.cs", "updated");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123", ["feat: add widget"], ["swival.toml", "src/app.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            sim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("manifest contains gitignored", result.Error, StringComparison.Ordinal);
        Assert.Contains("swival.toml", result.Error, StringComparison.Ordinal);
    }
}
