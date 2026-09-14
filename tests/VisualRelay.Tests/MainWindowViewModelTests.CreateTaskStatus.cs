using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    /// <summary>
    /// Creating a task updates the queue count the status line shows. Driven through the control API
    /// on the Mac, /state said "0 pending" beside two pending tasks until something else refreshed it.
    /// </summary>
    [Fact]
    public async Task CreateNewTaskCommand_WhenIdle_TheStatusCountsTheNewTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        foreach (var title in new[] { "First task", "Second task" })
        {
            viewModel.OpenNewTaskDialogCommand.Execute(null);
            viewModel.NewTaskTitle = title;
            await viewModel.CreateNewTaskCommand.ExecuteAsync(null);
        }

        Assert.Equal("2 pending", viewModel.StatusText);
    }

    /// <summary>A task created while a run is on leaves the run's status line alone.</summary>
    [Fact]
    public async Task CreateNewTaskCommand_WhileBusy_KeepsTheRunsStatus()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.IsBusy = true;
        viewModel.StatusText = "Running alpha";

        viewModel.OpenNewTaskDialogCommand.Execute(null);
        viewModel.NewTaskTitle = "Mid-run task";
        await viewModel.CreateNewTaskCommand.ExecuteAsync(null);

        Assert.Equal("Running alpha", viewModel.StatusText);
    }
}
