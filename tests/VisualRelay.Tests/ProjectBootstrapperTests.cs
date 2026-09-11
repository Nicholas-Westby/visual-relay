using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed class ProjectBootstrapperTests
{
    private static (TestRepository Repo, GitSimEngine Sim) CreateSimRepo()
    {
        var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        return (repo, sim);
    }

    [Fact]
    public async Task BootstrapAsync_EmptyFolder_MakesItRunnableWithPlaceholder()
    {
        var (repo, sim) = CreateSimRepo();

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);

        Assert.True(result.GitInitialized);
        Assert.True(result.UsedPlaceholderTestCommand);
        Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, result.TestCommand);
        Assert.True(result.HookInstalled);

        // The folder must now be runnable — Loaded, not Incomplete/Defaulted.
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);

        // HEAD resolves (worktrees work) and the authority hook is installed.
        var head = (await sim.RunAsync(repo.Root, ["rev-parse", "HEAD"], CancellationToken.None)).Output.Trim();
        Assert.NotEmpty(head);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public async Task BootstrapAsync_EstablishedRepo_WritesTheConfigWithoutCommittingIt()
    {
        // Bootstrap is a setup step, not an author: it leaves the operator's
        // history alone and the config uncommitted for them to decide about.
        var (repo, sim) = CreateSimRepo();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "src/app.cs", "code");
        var head = sim.Commit(repo.Root, "chore: seed repo");

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);

        Assert.False(result.GitInitialized);
        Assert.Equal(head, sim.Head(repo.Root));
        var tracked = (await sim.RunAsync(repo.Root, ["ls-files", "--", ".relay"], CancellationToken.None)).Output;
        Assert.Equal(string.Empty, tracked.Trim());
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task BootstrapAsync_PlaceholderCommand_IsTriviallyGreenOnThisMachine()
    {
        using var repo = TestRepository.Create();
        var validator = new TestCommandValidator(new DirectExecTestRunner(TimeSpan.FromSeconds(5)));

        var validation = await validator.ValidateAsync(repo.Root, ProjectBootstrapper.PlaceholderTestCommand);

        Assert.True(validation.Accepted, validation.RejectionReason);
        Assert.Equal(0, validation.RunResult.ExitCode);
    }

    [Fact]
    public async Task BootstrapAsync_DetectsRealToolchain_DoesNotUsePlaceholder()
    {
        var (repo, sim) = CreateSimRepo();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module example.com/m\n\ngo 1.22\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim, validationRunner: accepting);

        Assert.False(result.UsedPlaceholderTestCommand);
        Assert.Contains("go test", result.TestCommand);
    }

    [Fact]
    public async Task TryUpgrade_PlaceholderConfigGainsToolchain_AdoptsRealCommand()
    {
        var (repo, sim) = CreateSimRepo();
        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);
        // Simulate a scaffold task adding the toolchain marker.
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module example.com/m\n\ngo 1.22\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
            repo.Root, validationRunner: accepting, gitInvoker: sim);

        Assert.True(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);
        Assert.Contains("go test", loaded.Config.TestCommand);
    }

    /// <summary>
    /// The upgrade is also the first time the repo's test layout can be
    /// detected from real tracked source — bootstrap ran that detection
    /// against an empty tree and found nothing, so the upgrade must refresh
    /// it rather than leave that stale "nothing detected" snapshot in place.
    /// </summary>
    [Fact]
    public async Task TryUpgrade_PlaceholderConfigGainsToolchain_RefreshesTestLayout()
    {
        var (repo, sim) = CreateSimRepo();
        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);
        // Simulate a scaffold task adding a real, tracked Rust toolchain.
        sim.Seed(repo.Root, "Cargo.toml", "[package]\nname = \"m\"\n");
        sim.Seed(repo.Root, "src/lib.rs", "pub fn add(a: i32, b: i32) -> i32 { a + b }\n");
        sim.Seed(repo.Root, "src/main.rs", "fn main() {}\n");
        sim.Commit(repo.Root, "scaffold rust");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
            repo.Root, validationRunner: accepting, gitInvoker: sim);

        Assert.True(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal([".rs"], loaded.Config.AuthorTests.InlineTestExtensions);
    }

    [Fact]
    public async Task TryUpgrade_NonPlaceholderConfig_LeavesItUnchanged()
    {
        var (repo, sim) = CreateSimRepo();
        RelayConfigWriter.Write(repo.Root, "dotnet test");
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
            repo.Root, validationRunner: accepting, gitInvoker: sim);

        Assert.False(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal("dotnet test", loaded.Config.TestCommand);
    }

    [Fact]
    public async Task TryUpgrade_NoToolchainMarker_StaysPlaceholder()
    {
        var (repo, sim) = CreateSimRepo();
        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
            repo.Root, validationRunner: accepting, gitInvoker: sim);

        Assert.False(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, loaded.Config.TestCommand);
    }

    [Fact]
    public async Task TryUpgrade_PreservesOtherConfigKeys()
    {
        var (repo, sim) = CreateSimRepo();
        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);
        // Set an operator-changed key that the upgrade must preserve.
        RelayConfigWriter.UpsertSubagentTimeout(repo.Root, 900_000);
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
            repo.Root, validationRunner: accepting, gitInvoker: sim);

        Assert.True(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(900_000, loaded.Config.SubagentTimeoutMilliseconds); // preserved across the upgrade
        Assert.Contains("go test", loaded.Config.TestCommand);
    }
}
