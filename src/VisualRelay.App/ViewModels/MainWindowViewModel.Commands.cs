using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;

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

        // Manual Refresh also re-probes so the top-bar status dot stays current.
        await RefreshBackendStatusAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStartBackend))]
    private async Task StartBackendAsync()
    {
        // Best-effort one-click recovery: spawn the autostart script off the UI
        // thread, never throw, then re-probe so the dot reflects the result.
        try
        {
            var start = new ProcessStartInfo("tools/backend/backend.sh", "start")
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            if (process is not null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch
        {
            // Toolchain missing, script absent, etc. — leave the dot red.
            Debug.WriteLine("StartBackendAsync: backend.sh failed to start.");
        }

        await RefreshBackendStatusAsync();
    }

    private bool CanStartBackend() => !IsBackendReachable;

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
        RebuildAttachments(value);
        foreach (var task in Tasks)
        {
            task.IsSelected = ReferenceEquals(task, value);
        }

        // Exit edit mode when switching tasks.
        if (IsEditingMarkdown)
        {
            IsEditingMarkdown = false;
            EditBuffer = string.Empty;
        }

        // Exit new-task authoring when switching tasks.
        if (IsNewTaskDialogOpen)
        {
            IsNewTaskDialogOpen = false;
            NewTaskTitle = string.Empty;
            NewTaskBody = string.Empty;
            NewTaskError = null;
        }

        NotifyRunningTaskContextChanged();
        EditSelectedTaskCommand.NotifyCanExecuteChanged();
        AddAttachmentsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedTaskBoostsTurns));
        OnPropertyChanged(nameof(TurnBudgetLabel));
        OnPropertyChanged(nameof(CanToggleTurnBudget));
        _ = LoadSelectedTaskAsync(value);
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

        // Lazily promote active flat tasks to the nested subfolder layout
        // so the convention spreads incrementally without a bulk migration.
        if (!task.Task.IsNested && !task.Task.IsArchived)
        {
            var newPath = await RelayTaskWriter.PromoteToNestedAsync(RootPath, task.Task);
            var newDir = Path.GetDirectoryName(newPath)!;
            task.Task = task.Task with { IsNested = true, MarkdownPath = newPath, TaskDirectory = newDir };
        }

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
            RevealStageArtifactsCommand.NotifyCanExecuteChanged();
            return;
        }

        _selectedStageFilter = stage.Number;
        foreach (var item in Stages)
        {
            item.IsSelected = item.Number == stage.Number;
        }

        LogScopeLabel = $"stage {stage.Number:00}";
        ApplyLogFilter();
        RevealStageArtifactsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRevealStageArtifacts))]
    private void RevealStageArtifacts()
    {
        var target = SelectedStageRow?.RevealTarget;
        if (!string.IsNullOrEmpty(target))
        {
            FileReveal.Reveal(target);
        }
    }

    private bool CanRevealStageArtifacts() => !string.IsNullOrEmpty(SelectedStageRow?.RevealTarget);

    private StageRowViewModel? SelectedStageRow =>
        Stages.FirstOrDefault(stage => stage.Number == _selectedStageFilter);
}
