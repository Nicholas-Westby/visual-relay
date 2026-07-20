using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed class GitBootstrapperTests
{
    [Fact]
    public async Task EnsureRepositoryAsync_EmptyFolder_InitializesRepoWithResolvableHead()
    {
        // Asserts a REAL on-disk .git directory — GitSim never touches the
        // filesystem for repo metadata, so this fact genuinely needs the real
        // git binary and cannot be simulated.
        SlowIntegration.SkipIfNotOptedIn();
        using var repo = TestRepository.Create(); // a plain dir, not a git repo

        var initialized = await GitBootstrapper.EnsureRepositoryAsync(repo.Root);

        Assert.True(initialized);
        Assert.True(Directory.Exists(Path.Combine(repo.Root, ".git")));
        // HEAD must resolve — PlanningWorktree does `git worktree add --detach <p> HEAD`,
        // which fails against an unborn HEAD.
        var head = (await new GitInvoker().RunAsync(repo.Root, ["rev-parse", "HEAD"], CancellationToken.None)).Output.Trim();
        Assert.NotEmpty(head);
    }

    [Fact]
    public async Task EnsureRepositoryAsync_ExistingRepoWithCommit_ReturnsFalse_AddsNoCommit()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "a.txt", "hi");
        sim.Commit(repo.Root, "first");
        var before = sim.Head(repo.Root);

        var initialized = await GitBootstrapper.EnsureRepositoryAsync(repo.Root, sim);

        Assert.False(initialized);
        var after = sim.Head(repo.Root);
        Assert.Equal(before, after); // must not inject a commit into an established repo
    }

    [Fact]
    public async Task EnsureRepositoryAsync_RepoWithUnbornHead_CreatesInitialCommit()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root); // a repo, but zero commits → unborn HEAD

        var initialized = await GitBootstrapper.EnsureRepositoryAsync(repo.Root, sim);

        Assert.False(initialized); // already a repo, did not create one
        var head = sim.Head(repo.Root)!;
        Assert.NotEmpty(head); // but HEAD now resolves
    }

    [Fact]
    public async Task IsRepositoryAsync_DistinguishesRepoFromPlainDir()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        Assert.False(await GitBootstrapper.IsRepositoryAsync(repo.Root, sim));

        sim.InitRepo(repo.Root);
        Assert.True(await GitBootstrapper.IsRepositoryAsync(repo.Root, sim));
    }
}
