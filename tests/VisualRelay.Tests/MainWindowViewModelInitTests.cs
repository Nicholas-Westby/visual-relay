using VisualRelay.App.ViewModels;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelInitTests
{
    [Fact]
    public async Task RunSelected_WithNoConfig_BlocksAndFlagsInitialization()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n"); // no WriteConfig
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.NeedsInitialization);
        Assert.Equal("alpha", Assert.Single(viewModel.Tasks).Id);

        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.RunSelectedCommand.ExecuteAsync(null);

        Assert.True(viewModel.NeedsInitialization);
        Assert.Contains("initialize", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task NoConfig_PrefillsDetectedTestCommand()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.NeedsInitialization);
        Assert.Equal("dotnet test", viewModel.InitTestCommandInput);
    }

    [Fact]
    public async Task CreateConfig_WritesConfigAndPopulatesQueue()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.False(viewModel.NeedsInitialization);
        Assert.Equal("alpha", Assert.Single(viewModel.Tasks).Id);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task FindTestCommand_PopulatesInputFromFinder()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            TestCommandFinder = new LlmTestCommandFinder((_, _) => Task.FromResult("go test ./..."))
        };
        await viewModel.LoadInitialAsync();

        await viewModel.FindTestCommandCommand.ExecuteAsync(null);

        Assert.Equal("go test ./...", viewModel.InitTestCommandInput);
    }
}
