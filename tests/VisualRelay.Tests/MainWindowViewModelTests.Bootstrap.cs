using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Greenfield bootstrap wiring: the GUI can make an empty folder runnable, and the
// run gate adopts the real test command once a placeholder project gains a toolchain.
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task BootstrapProjectCommand_EmptyFolder_MakesItRunnable()
    {
        using var repo = TestRepository.Create(); // empty: no git, no config

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization); // precondition: not runnable yet

        await viewModel.BootstrapProjectCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(Path.Combine(repo.Root, ".git")));
        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);
        Assert.False(viewModel.NeedsInitialization); // refresh cleared the init banner
    }

    [Fact]
    public async Task EnsureRunnableAsync_UpgradesPlaceholder_WhenToolchainAppears()
    {
        using var repo = TestRepository.Create();
        // Greenfield: bootstrap to a placeholder, then a "scaffold task" added a toolchain
        // marker. package.json's scripts.test = "true" keeps the upgrade's real validation
        // hermetic (no node needed — the detected command IS "true", which always exits 0).
        await ProjectBootstrapper.BootstrapAsync(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{\"scripts\":{\"test\":\"true\"}}");

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.IsHuggingFaceConfigured = true;

        await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        var loaded = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal("true", loaded.Config.TestCommand); // adopted the detected command
        Assert.NotEqual(ProjectBootstrapper.PlaceholderTestCommand, loaded.Config.TestCommand);
    }
}
