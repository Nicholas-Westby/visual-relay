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
        Assert.Equal("Stage 05 · Author-tests", row.MetricsLine);
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
        Assert.Equal("Stage 05 · Author-tests", row.MetricsLine);
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

    [Fact]
    public void TaskRow_RunningElapsedLabel_IsEmptyAfterMarkRunning()
    {
        var task = new RelayTaskItem("task-x", "/tmp/llm-tasks/task-x.md", "/tmp/llm-tasks", false, []);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");

        Assert.True(row.IsRunning);
        Assert.Equal("", row.RunningElapsedLabel);
        Assert.Equal("Stage 05 · Author-tests", row.MetricsLine);
    }

    [Fact]
    public void TaskRow_MetricsLine_IncludesElapsedWhenLabelIsSet()
    {
        var task = new RelayTaskItem("task-x", "/tmp/llm-tasks/task-x.md", "/tmp/llm-tasks", false, []);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");
        row.RunningElapsedLabel = "1m 04s";

        Assert.Equal("Stage 05 · Author-tests · 1m 04s", row.MetricsLine);
    }

    [Fact]
    public void TaskRow_MarkIdle_ClearsRunningElapsedLabelAndRestoresMetricsLine()
    {
        var task = new RelayTaskItem("task-x", "/tmp/llm-tasks/task-x.md", "/tmp/llm-tasks", false,
            [], CompletedStageCount: 3, DurationSeconds: 120, CostUsd: 0.003);
        var row = new TaskRowViewModel(task);

        row.MarkRunning(5, "Author-tests");
        row.RunningElapsedLabel = "2m 36s";
        Assert.Equal("Stage 05 · Author-tests · 2m 36s", row.MetricsLine);

        row.MarkIdle();

        Assert.False(row.IsRunning);
        Assert.Equal("", row.RunningElapsedLabel);
        Assert.Equal(task.MetricsLine, row.MetricsLine);
    }

    private static string ColorOf(IBrush brush)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        return solid.Color.ToString();
    }
}
