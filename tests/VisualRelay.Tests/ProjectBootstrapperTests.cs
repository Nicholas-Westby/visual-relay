using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ProjectBootstrapperTests
{
    [Fact]
    public async Task BootstrapAsync_EmptyFolder_MakesItRunnableWithPlaceholder()
    {
        using var repo = TestRepository.Create();

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root);

        Assert.True(result.GitInitialized);
        Assert.True(result.UsedPlaceholderTestCommand);
        Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, result.TestCommand);
        Assert.True(result.HookInstalled);

        // The folder must now be runnable — Loaded, not Incomplete/Defaulted.
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);

        // HEAD resolves (worktrees work) and the authority hook is installed.
        Assert.NotEmpty(TestGit.Run(repo.Root, "rev-parse", "HEAD").Trim());
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
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module example.com/m\n\ngo 1.22\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: accepting);

        Assert.False(result.UsedPlaceholderTestCommand);
        Assert.Contains("go test", result.TestCommand);
    }

    [Fact]
    public async Task TryUpgrade_PlaceholderConfigGainsToolchain_AdoptsRealCommand()
    {
        using var repo = TestRepository.Create();
        await ProjectBootstrapper.BootstrapAsync(repo.Root);
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
        using var repo = TestRepository.Create();
        await ProjectBootstrapper.BootstrapAsync(repo.Root);
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(repo.Root, accepting);

        Assert.False(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, loaded.Config.TestCommand);
    }

    [Fact]
    public async Task TryUpgrade_PreservesOtherConfigKeys()
    {
        using var repo = TestRepository.Create();
        await ProjectBootstrapper.BootstrapAsync(repo.Root);
        RelayConfigWriter.UpsertBypassSandbox(repo.Root, true);
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var upgraded = await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(repo.Root, accepting);

        Assert.True(upgraded);
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.True(loaded.Config.BypassSandbox); // preserved across the upgrade
        Assert.Contains("go test", loaded.Config.TestCommand);
    }
}
