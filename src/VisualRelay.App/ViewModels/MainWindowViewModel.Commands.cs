using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var selected = await _folderPicker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        RootPath = selected;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusText = "Refreshing";
            await ReloadTaskListAsync();
            StatusText = FormatQueueStatus();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private async Task RunSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var task = SelectedTask;
        await RunBusyAsync(async () =>
        {
            await RunOneAsync(task);
            await ReloadTaskListAsync(task.Id);
        });
    }

    [RelayCommand(CanExecute = nameof(CanDrain))]
    private async Task DrainQueueAsync()
    {
        if (PauseRequested)
        {
            StatusText = "Paused: no new task will start";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var circuitBreaker = new DrainCircuitBreaker();
            DrainCircuitBreaker.ClearHaltMarker(RootPath);
            var queue = Tasks.Where(task => !task.NeedsReview).ToList();
            while (queue.FirstOrDefault() is { } task && !PauseRequested)
            {
                SelectedTask = task;
                var outcome = await RunOneAsync(task);
                queue.Remove(task);
                if (!ShowArchive)
                {
                    Tasks.Remove(task);
                }

                if (circuitBreaker.ShouldHalt(RootPath, outcome))
                {
                    StatusText = $"Drain halted: {circuitBreaker.HaltMessage ?? "task needs review"}";
                    await RefreshTasksAfterDrainAsync(outcome.TaskId);
                    return;
                }
            }

            StatusText = PauseRequested ? "Paused at task boundary" : "Queue drained";
            await RefreshTasksAfterDrainAsync();
        });
    }

    [RelayCommand]
    private void TogglePause()
    {
        PauseRequested = !PauseRequested;
        StatusText = PauseRequested
            ? IsBusy ? $"Pause armed: finishing {_runningTaskId ?? "current task"} before stopping" : "Paused: no new task will start"
            : IsBusy ? $"Running {_runningTaskId ?? "task"}" : FormatQueueStatus();
    }

    [RelayCommand(CanExecute = nameof(CanFollowRunningTask))]
    private async Task FollowRunningTaskAsync()
    {
        if (_runningTaskId is not { } taskId)
        {
            return;
        }

        if (ShowArchive)
        {
            ShowArchive = false;
            await ReloadTaskListAsync(taskId);
            return;
        }

        SelectedTask = Tasks.FirstOrDefault(task => task.Id == taskId);
        if (SelectedTask is null)
        {
            await ReloadTaskListAsync(taskId);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index > 0)
        {
            Tasks.Move(index, index - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleArchive))]
    private async Task ToggleArchiveAsync()
    {
        ShowArchive = !ShowArchive;
        await ReloadTaskListAsync();
        StatusText = PauseRequested ? "Paused: no new task will start" : FormatQueueStatus();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index >= 0 && index < Tasks.Count - 1)
        {
            Tasks.Move(index, index + 1);
        }
    }

    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        _selectedStageFilter = null;
        LogScopeLabel = "full";
        foreach (var task in Tasks)
        {
            task.IsSelected = ReferenceEquals(task, value);
        }

        NotifyRunningTaskContextChanged();
        _ = LoadSelectedTaskAsync(value);
    }

    private async Task<RelayTaskOutcome> RunOneAsync(TaskRowViewModel task)
    {
        ResetStages();
        ClearLogState();
        StatusText = $"Running {task.Id}";
        BeginRunningTask(task);
        NotifyPauseStateChanged();
        var config = await RelayConfigLoader.LoadAsync(RootPath);
        var sink = new ObservableRelayEventSink(HandleRelayEvent);
        var dependencies = new RelayDriverDependencies(new SwivalSubagentRunner(config, eventSink: sink), new ShellTestRunner(), sink);
        var driver = new RelayDriver(dependencies, RelayDriverOptions.Default);
        try
        {
            var outcome = await driver.RunTaskAsync(RootPath, task.Id);
            StatusText = outcome.Status == RelayTaskOutcomeStatus.Committed ? $"Committed {task.Id}" : $"Flagged {task.Id}";
            await LoadRunHistoryAsync(task.Id);
            if (PauseRequested)
            {
                StatusText = "Paused at task boundary";
            }

            return outcome;
        }
        finally
        {
            ClearRunningTask(task.Id);
            NotifyPauseStateChanged();
        }
    }

    private async Task LoadSelectedTaskAsync(TaskRowViewModel? task)
    {
        if (task is null)
        {
            SelectedTaskMarkdown = string.Empty;
            SelectedTaskContext = string.Empty;
            SelectedTaskMetricLabel = "No run history";
            SelectedTaskError = null;
            ClearLogState();
            ResetStages();
            return;
        }

        ResetStages();
        var input = await new RelayTaskRepository(RootPath).ReadTaskInputAsync(task.Task);
        SelectedTaskMarkdown = input.Markdown;
        SelectedTaskContext = input.Context ?? string.Empty;
        await LoadRunHistoryAsync(task.Id);
    }

    [RelayCommand]
    private void SelectStage(StageRowViewModel stage)
    {
        if (_selectedStageFilter == stage.Number)
        {
            _selectedStageFilter = null;
            stage.IsSelected = false;
            LogScopeLabel = "full";
            ApplyLogFilter();
            return;
        }

        _selectedStageFilter = stage.Number;
        foreach (var item in Stages)
        {
            item.IsSelected = item.Number == stage.Number;
        }

        LogScopeLabel = $"stage {stage.Number:00}";
        ApplyLogFilter();
    }
}
