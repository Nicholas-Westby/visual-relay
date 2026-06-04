using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelTests
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
        await WaitUntilAsync(() => viewModel.TraceEntries.Count == 2);

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
    public void MoveCommands_TrackSelectedTaskPosition()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Tasks.Add(new TaskRowViewModel(new("a", "/tmp/llm-tasks/a.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("b", "/tmp/llm-tasks/b.md", "/tmp/llm-tasks", false, [])));
        viewModel.Tasks.Add(new TaskRowViewModel(new("c", "/tmp/llm-tasks/c.md", "/tmp/llm-tasks", false, [])));

        viewModel.SelectedTask = viewModel.Tasks[0];
        Assert.False(viewModel.MoveUpCommand.CanExecute(null));
        Assert.True(viewModel.MoveDownCommand.CanExecute(null));

        viewModel.SelectedTask = viewModel.Tasks[1];
        Assert.True(viewModel.MoveUpCommand.CanExecute(null));
        Assert.True(viewModel.MoveDownCommand.CanExecute(null));

        viewModel.SelectedTask = viewModel.Tasks[2];
        Assert.True(viewModel.MoveUpCommand.CanExecute(null));
        Assert.False(viewModel.MoveDownCommand.CanExecute(null));

        viewModel.SelectedTask = null;
        Assert.False(viewModel.MoveUpCommand.CanExecute(null));
        Assert.False(viewModel.MoveDownCommand.CanExecute(null));
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
        await WaitUntilAsync(() => viewModel.HasSelectedTaskError);

        Assert.True(viewModel.HasSelectedTaskError);
        Assert.Equal("the runner exploded", viewModel.SelectedTaskError);

        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == "clean");
        await WaitUntilAsync(() => !viewModel.HasSelectedTaskError);

        Assert.False(viewModel.HasSelectedTaskError);
        Assert.True(string.IsNullOrEmpty(viewModel.SelectedTaskError));
    }

    private static void WriteErroredReport(string root, string taskId, int stage, string errorMessage)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, $"stage{stage}-attempt1.report.json"),
            $$"""
            {
              "timestamp": "2026-05-31T20:00:0{{stage}}+00:00",
              "model": "cheap-kimi",
              "result": { "outcome": "error", "exit_code": 1, "error_message": "{{errorMessage}}" },
              "stats": { "total_llm_time_s": {{stage}}, "prompt_cache": { "cached_tokens": 0 } },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
            }
            """);
    }

    private static void WriteReportAndTrace(string root, string taskId, int stage, string content)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, $"stage{stage}-attempt1.report.json"),
            $$"""
            {
              "timestamp": "2026-05-31T20:00:0{{stage}}+00:00",
              "model": "cheap-kimi",
              "result": { "answer": "{{content}}" },
              "stats": { "total_llm_time_s": {{stage}}, "prompt_cache": { "cached_tokens": 0 } },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": 1000 }]
            }
            """);
        var traceDirectory = Path.Combine(taskDirectory, $"stage{stage}-attempt1");
        Directory.CreateDirectory(traceDirectory);
        File.WriteAllText(
            Path.Combine(traceDirectory, $"{Guid.NewGuid():N}.jsonl"),
            $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{content}\"}}]}}}}\n");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
