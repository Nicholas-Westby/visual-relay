using VisualRelay.App.ViewModels;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task TogglePauseCommand_PausesActiveDrainAfterCurrentTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        runner.AfterRun = () => viewModel.TogglePauseCommand.Execute(null);
        viewModel.SetActiveDrainControllerForTests(controller);
        await controller.DrainAsync();
        Assert.Equal(["alpha"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Paused, controller.State);
        Assert.True(viewModel.PauseRequested);
    }
}
