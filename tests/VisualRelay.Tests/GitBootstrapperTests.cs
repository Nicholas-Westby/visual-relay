using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class GitBootstrapperTests
{
    [Fact]
    public async Task EnsureRepositoryAsync_EmptyFolder_InitializesRepoWithResolvableHead()
    {
        using var repo = TestRepository.Create(); // a plain dir, not a git repo

        var initialized = await GitBootstrapper.EnsureRepositoryAsync(repo.Root);

        Assert.True(initialized);
        Assert.True(Directory.Exists(Path.Combine(repo.Root, ".git")));
        // HEAD must resolve — PlanningWorktree does `git worktree add --detach <p> HEAD`,
        // which fails against an unborn HEAD. (TestGit.Run asserts git exit 0.)
        var head = TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim();
        Assert.NotEmpty(head);
    }

    [Fact]
    public async Task EnsureRepositoryAsync_ExistingRepoWithCommit_ReturnsFalse_AddsNoCommit()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "hi");
        TestGit.Run(repo.Root, "add", "-A");
        TestGit.Run(repo.Root, "commit", "-m", "first");
        var before = TestGit.Run(repo.Root, "rev-list", "--count", "HEAD").Trim();

        var initialized = await GitBootstrapper.EnsureRepositoryAsync(repo.Root);

        Assert.False(initialized);
        var after = TestGit.Run(repo.Root, "rev-list", "--count", "HEAD").Trim();
        Assert.Equal(before, after); // must not inject a commit into an established repo
    }

    [Fact]
    public async Task EnsureRepositoryAsync_RepoWithUnbornHead_CreatesInitialCommit()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init"); // a repo, but zero commits → unborn HEAD

        var initialized = await GitBootstrapper.EnsureRepositoryAsync(repo.Root);

        Assert.False(initialized); // already a repo, did not create one
        var head = TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim();
        Assert.NotEmpty(head); // but HEAD now resolves
    }

    [Fact]
    public async Task IsRepositoryAsync_DistinguishesRepoFromPlainDir()
    {
        using var repo = TestRepository.Create();
        Assert.False(await GitBootstrapper.IsRepositoryAsync(repo.Root));

        TestGit.Run(repo.Root, "init");
        Assert.True(await GitBootstrapper.IsRepositoryAsync(repo.Root));
    }
}
