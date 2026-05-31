using Avalonia.Media;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class LiveStateViewModelTests
{
    [Fact]
    public async Task MainWindow_ReconcilesRunningTaskAcrossTaskListReload()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("nested", "# Nested\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.RestoreRunningTaskState("nested", 5, "Author-tests");

        await viewModel.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(viewModel.Tasks, task => task.Id == "nested");
        Assert.True(row.IsRunning);
        Assert.Equal("Step 05 · Author-tests", row.MetricsLine);
    }

    [Fact]
    public async Task MainWindow_ExposesFollowActionWhenSelectionDiffersFromRunningTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("nested", "# Nested\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.RestoreRunningTaskState("nested", 8, "Fix");
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "alpha");

        Assert.True(viewModel.IsViewingDifferentTaskDuringRun);
        Assert.Equal("Viewing alpha · running nested", viewModel.ViewingRunContextText);
        Assert.True(viewModel.FollowRunningTaskCommand.CanExecute(null));

        await viewModel.FollowRunningTaskCommand.ExecuteAsync(null);

        Assert.Equal("nested", viewModel.SelectedTask?.Id);
        Assert.False(viewModel.IsViewingDifferentTaskDuringRun);
    }

    [Fact]
    public void TaskRow_RunningStateOverridesPersistedTaskState()
    {
        var task = new RelayTaskItem("add-multiply", "/tmp/llm-tasks/add-multiply.md", "/tmp/llm-tasks", false, []);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");

        Assert.True(row.IsRunning);
        Assert.Equal("Running", row.StateLabel);
        Assert.Equal("Step 05 · Author-tests", row.MetricsLine);
        Assert.Equal("#ff5ad47d", ColorOf(row.AccentBrush));
    }

    [Fact]
    public void StageRow_RunningStyleTakesPrecedenceOverLogFilterSelection()
    {
        var stage = new StageRowViewModel(new RelayStageDefinition(6, "Implement", "balanced", "llm", "all", "all", "system", "{}"))
        {
            Status = "Running",
            IsSelected = true
        };

        Assert.Equal("Running", stage.StatusLabel);
        Assert.Equal("#ff5ad47d", ColorOf(stage.BorderBrush));
    }

    private static string ColorOf(IBrush brush)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        return solid.Color.ToString();
    }
}
