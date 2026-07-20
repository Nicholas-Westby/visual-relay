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

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(repo.Root, accepting);

        Assert.True(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);
        Assert.Contains("go test", loaded.Config.TestCommand);
    }

    [Fact]
    public async Task TryUpgrade_NonPlaceholderConfig_LeavesItUnchanged()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(repo.Root, accepting);

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

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(repo.Root, accepting);

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
        RelayConfigWriter.UpsertCommitProofArtifacts(repo.Root, false);
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(repo.Root, accepting);

        Assert.True(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.False(loaded.Config.CommitProofArtifacts); // preserved across the upgrade
        Assert.Contains("go test", loaded.Config.TestCommand);
    }
}
