using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task SelectStageCommand_TogglesBetweenStageLogAndFullTaskLog()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        WriteReportAndTrace(repo.Root, "alpha", 1, "stage one");
        WriteReportAndTrace(repo.Root, "alpha", 2, "stage two");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.LastSelectionLoad!;
        Assert.Equal("full", viewModel.LogScopeLabel);
        Assert.Equal(2, viewModel.Events.Count);
        viewModel.SelectStageCommand.Execute(viewModel.Stages[0]);
        Assert.Equal("stage 01", viewModel.LogScopeLabel);
        Assert.Single(viewModel.Events);
        Assert.All(viewModel.Events, item => Assert.Equal(1, item.StageNumber));
        Assert.Single(viewModel.TraceEntries);
        Assert.Contains("stage one", viewModel.TraceEntries[0].Content);
        Assert.True(viewModel.Stages[0].IsSelected);
        viewModel.SelectStageCommand.Execute(viewModel.Stages[0]);
        Assert.Equal("full", viewModel.LogScopeLabel);
        Assert.Equal(2, viewModel.Events.Count);
        Assert.False(viewModel.Stages[0].IsSelected);
    }

    [Fact]
    public async Task ToggleArchiveCommand_LoadsCompletedTasksAndDisablesRunActions()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("pending", "# Pending\n");
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completed);
        await File.WriteAllTextAsync(Path.Combine(completed, "DONE-finished.md"), "# Finished\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.Equal("QUEUE", viewModel.TaskListTitle);
        Assert.Equal("pending", Assert.Single(viewModel.Tasks).Id);
        await viewModel.ToggleArchiveCommand.ExecuteAsync(null);
        Assert.Equal("ARCHIVE", viewModel.TaskListTitle);
        var archived = Assert.Single(viewModel.Tasks);
        Assert.Equal("finished", archived.Id);
        Assert.True(archived.IsArchived);
        Assert.False(viewModel.RunSelectedCommand.CanExecute(null));
        Assert.False(viewModel.DrainQueueCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToggleArchiveCommand_CanLoadArchiveWhileRunnerIsBusy()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("pending", "# Pending\n");
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completed);
        await File.WriteAllTextAsync(Path.Combine(completed, "DONE-finished.md"), "# Finished\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.IsBusy = true;
        Assert.True(viewModel.ToggleArchiveCommand.CanExecute(null));
        await viewModel.ToggleArchiveCommand.ExecuteAsync(null);
        Assert.True(viewModel.ShowArchive);
        Assert.Equal("finished", Assert.Single(viewModel.Tasks).Id);
    }

    [Fact]
    public void TogglePauseCommand_ShowsTaskBoundarySemanticsAndBlocksNewRuns()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Tasks.Add(new TaskRowViewModel(new("alpha", "/tmp/llm-tasks/alpha.md", "/tmp/llm-tasks", false, [])));
        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.Equal("Pause after task", viewModel.PauseButtonText);
        Assert.False(viewModel.IsPauseNoticeVisible);
        Assert.True(viewModel.DrainQueueCommand.CanExecute(null));
        viewModel.TogglePauseCommand.Execute(null);
        Assert.True(viewModel.PauseRequested);
        Assert.Equal("Resume", viewModel.PauseButtonText);
        Assert.True(viewModel.IsPauseNoticeVisible);
        Assert.Equal("Paused before next task", viewModel.PauseNoticeText);
        Assert.Equal("Paused: no new task will start", viewModel.StatusText);
        Assert.False(viewModel.DrainQueueCommand.CanExecute(null));
        Assert.False(viewModel.RunSelectedCommand.CanExecute(null));
        viewModel.TogglePauseCommand.Execute(null);
        Assert.False(viewModel.PauseRequested);
        Assert.Equal("Pause after task", viewModel.PauseButtonText);
        Assert.False(viewModel.IsPauseNoticeVisible);
        Assert.True(viewModel.DrainQueueCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingTask_SurfacesErrorFromFailedLatestRunAndClearsOnCleanTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("broken", "# Broken\n");
        repo.WriteTask("clean", "# Clean\n");
        WriteErroredReport(repo.Root, "broken", 1, "the runner exploded");
        WriteReportAndTrace(repo.Root, "clean", 1, "all good");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("the runner exploded", viewModel.SelectedTaskError);
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "clean");
        await viewModel.LastSelectionLoad!;
        Assert.False(viewModel.HasSelectedTaskError);
        Assert.True(string.IsNullOrEmpty(viewModel.SelectedTaskError));
    }

    [Fact]
    public async Task StartingRunOnPreviouslyFailedTask_ClearsStaleErrorAndRestoresAfterRunSettles()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("broken", "# Broken\n");
        WriteErroredReport(repo.Root, "broken", 1, "the runner exploded");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("the runner exploded", viewModel.SelectedTaskError);
        viewModel.RestoreRunningTaskState("broken", 1, "Research");
        viewModel.SelectedTask = null;
        await viewModel.LastSelectionLoad!;
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.False(viewModel.HasSelectedTaskError);
        Assert.True(string.IsNullOrEmpty(viewModel.SelectedTaskError));
        viewModel.RestoreRunningTaskState("_cleared_", null, null);
        viewModel.SelectedTask = null;
        await viewModel.LastSelectionLoad!;
        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("the runner exploded", viewModel.SelectedTaskError);
    }

    [Fact]
    public async Task RevealStageArtifactsCommand_DisabledUntilStageWithArtifactsIsSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        WriteReportAndTrace(repo.Root, "alpha", 1, "stage one");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.LastSelectionLoad!;
        Assert.False(viewModel.RevealStageArtifactsCommand.CanExecute(null));
        viewModel.SelectStageCommand.Execute(viewModel.Stages[0]);
        Assert.True(viewModel.RevealStageArtifactsCommand.CanExecute(null));
        viewModel.SelectStageCommand.Execute(viewModel.Stages[0]);
        Assert.False(viewModel.RevealStageArtifactsCommand.CanExecute(null));
    }

    [Fact]
    public async Task DrainQueueCommand_FiresCanExecuteChanged_AfterCreatingTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.False(viewModel.DrainQueueCommand.CanExecute(null));
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        viewModel.NewTaskTitle = "New feature";
        var changedCount = 0;
        viewModel.DrainQueueCommand.CanExecuteChanged += (_, _) => changedCount++;
        await viewModel.CreateNewTaskCommand.ExecuteAsync(null);
        Assert.True(changedCount >= 1,
            "DrainQueueCommand.CanExecuteChanged must fire after creating a task " +
            "(ReloadTaskListAsync must re-notify DrainQueueCommand).");
        Assert.True(viewModel.DrainQueueCommand.CanExecute(null),
            "The 'Run All' button must become enabled once a task exists.");
    }

    [Fact]
    public void DrainQueueCommand_CanExecute_WhenAllTasksHaveNeedsReview()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Tasks.Add(new TaskRowViewModel(new RelayTaskItem(
            "alpha", "/tmp/llm-tasks/alpha.md", "/tmp/llm-tasks", false, [],
            ReviewReason: "author-tests did not go red")));
        Assert.True(viewModel.Tasks[0].NeedsReview);
        Assert.True(viewModel.DrainQueueCommand.CanExecute(null),
            "The 'Run All' button must be enabled when tasks exist, even if they all have errors.");
    }

    [Fact]
    public async Task SelectingCleanRunningTaskAfterFlagged_DoesNotShowStaleErrorOrEnableCreateFix()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("broken", "# Broken\n");
        repo.WriteTask("clean", "# Clean\n");
        WriteErroredReport(repo.Root, "broken", 1, "the runner exploded");
        WriteReportAndTrace(repo.Root, "clean", 1, "all good");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "broken");
        await viewModel.LastSelectionLoad!;
        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("the runner exploded", viewModel.SelectedTaskError);
        Assert.True(viewModel.CreateFixTaskCommand.CanExecute(null));
        viewModel.RestoreRunningTaskState("clean", 5, "Implement");
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "clean");
        await viewModel.LastSelectionLoad!;
        Assert.False(viewModel.HasSelectedTaskError);
        Assert.True(string.IsNullOrEmpty(viewModel.SelectedTaskError));
        Assert.False(viewModel.CreateFixTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectTaskWithPriorRun_LoadsStageStatusesFromDisk()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var statusDir = Path.Combine(repo.Root, ".relay", "alpha");
        Directory.CreateDirectory(statusDir);
        var statusEntries = new[] { new StageStatusEntry(1, "Ideate", "Done"), new StageStatusEntry(2, "Research", "Flagged", Error: "something went wrong") };
        await StageStatusRecord.WriteAsync(statusDir, statusEntries);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.LastSelectionLoad!;
        Assert.Equal("Done", viewModel.Stages[0].Status);
        Assert.Equal("Flagged", viewModel.Stages[1].Status);
        Assert.Equal("Waiting", viewModel.Stages[2].Status);
    }

    [Fact]
    public async Task RestartClearsStageBoard_DoesNotLoadStaleStatusFromDisk()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var statusDir = Path.Combine(repo.Root, ".relay", "alpha");
        Directory.CreateDirectory(statusDir);
        var statusEntries = new[] { new StageStatusEntry(1, "Ideate", "Done"), new StageStatusEntry(2, "Research", "Done") };
        await StageStatusRecord.WriteAsync(statusDir, statusEntries);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        await viewModel.LastSelectionLoad!;
        Assert.Equal("Done", viewModel.Stages[0].Status);
        Assert.Equal("Done", viewModel.Stages[1].Status);
        Assert.Equal("Waiting", viewModel.Stages[2].Status);

        viewModel.SelectedTask = null;
        await viewModel.LastSelectionLoad!;
        Assert.All(viewModel.Stages, stage => Assert.Equal("Waiting", stage.Status));

        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.LastSelectionLoad!;
        Assert.Equal("Done", viewModel.Stages[0].Status);
        Assert.Equal("Done", viewModel.Stages[1].Status);
        Assert.Equal("Waiting", viewModel.Stages[2].Status);
    }

    private static void WriteErroredReport(string root, string taskId, int stage, string errorMessage)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        var json = "{\"result\":{\"outcome\":\"error\",\"error_message\":\"" + errorMessage + "\"},\"stats\":{\"total_llm_time_s\":" + stage + "}}";
        File.WriteAllText(Path.Combine(taskDirectory, $"stage{stage}-attempt1.report.json"), json);
        var statusEntries = new[] { new StageStatusEntry(stage, $"Stage {stage}", "Flagged", Error: errorMessage) };
        StageStatusRecord.WriteAsync(taskDirectory, statusEntries).GetAwaiter().GetResult();
    }

    private static void WriteReportAndTrace(string root, string taskId, int stage, string content)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        var json = "{\"result\":{\"answer\":\"" + content + "\"},\"stats\":{\"total_llm_time_s\":" + stage + "}}";
        File.WriteAllText(Path.Combine(taskDirectory, $"stage{stage}-attempt1.report.json"), json);
        var traceDirectory = Path.Combine(taskDirectory, $"stage{stage}-attempt1");
        Directory.CreateDirectory(traceDirectory);
        File.WriteAllText(
            Path.Combine(traceDirectory, $"{Guid.NewGuid():N}.jsonl"),
            $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{content}\"}}]}}}}\n");
        var statusEntries = new[] { new StageStatusEntry(stage, $"Stage {stage}", "Done") };
        StageStatusRecord.WriteAsync(taskDirectory, statusEntries).GetAwaiter().GetResult();
    }
}
