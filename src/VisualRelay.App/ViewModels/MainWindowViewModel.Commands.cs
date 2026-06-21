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
            Trace.WriteLine("StartBackendAsync: backend.sh failed to start.");
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

    [RelayCommand(CanExecute = nameof(CanToggleArchive))]
    private async Task ToggleArchiveAsync()
    {
        ShowArchive = !ShowArchive;
        await ReloadTaskListAsync();
        StatusText = PauseRequested ? "Paused: no new task will start" : FormatQueueStatus();
    }

    /// <summary>
    /// Testable reorder seam: moves the task at <paramref name="fromIndex"/> to
    /// <paramref name="toIndex"/> in the in-memory <see cref="Tasks"/> list,
    /// keeps the moved row selected, and is a no-op while busy or showing the
    /// archive (mirroring the old Up/Down gate). The drag gesture in
    /// <c>QueuePanel</c> routes its mutation through here so the logic stays
    /// unit-testable without driving pointer input.
    /// </summary>
    internal void MoveTask(int fromIndex, int toIndex)
    {
        if (IsBusy || ShowArchive)
        {
            return;
        }

        if (fromIndex < 0 || fromIndex >= Tasks.Count ||
            toIndex < 0 || toIndex >= Tasks.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        var moved = Tasks[fromIndex];
        Tasks.Move(fromIndex, toIndex);
        SelectedTask = moved;

        // Persist the new manual order so Run All, Refresh, and an app restart all
        // honor it instead of resetting to alphabetical. Best-effort inside the
        // store — a failed write never escapes into the drag gesture.
        new TaskOrderStore(RootPath).Save(Tasks.Select(task => task.Id));
    }

    /// <summary>
    /// Captures the in-flight selection-load task so tests can
    /// <c>await viewModel.LastSelectionLoad</c> instead of polling
    /// derived properties on a wall-clock budget (which false-fails
    /// under CPU load).  Set by <see cref="OnSelectedTaskChanged(TaskRowViewModel?)"/>
    /// (the generated synchronous setter hook for
    /// <see cref="SelectedTask"/>) and never null after the first
    /// assignment.
    /// </summary>
    internal Task? LastSelectionLoad { get; private set; }

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

        // Notify rewrite-related bind targets on task selection change.
        OnPropertyChanged(nameof(IsSelectedTaskRewriting));
        OnPropertyChanged(nameof(SelectedTaskRewriteElapsed));
        OnPropertyChanged(nameof(SelectedTaskHasRewriteUndo));
        OnPropertyChanged(nameof(CanRewriteSelectedPublic));
        RewriteSelectedTaskCommand.NotifyCanExecuteChanged();
        RevertRewriteSelectedCommand.NotifyCanExecuteChanged();

        // Capture the task so tests can await it deterministically
        // (no 1 000 ms wall-clock budget — the real operation decides).
        // Faults are surfaced to StatusText (the VM's operation-error
        // channel) instead of being silently swallowed into _.
        LastSelectionLoad = SelectTaskAsync(value);
    }

    /// <summary>
    /// Loads the selected task's markdown, context, and run history.
    /// Awaitable by tests via <see cref="LastSelectionLoad"/>.
    /// Runtime behavior is unchanged on success; a load fault is
    /// surfaced to <see cref="MainWindowViewModel.StatusText"/>
    /// (the VM's established operation-error channel) rather than
    /// being discarded as an unobserved task exception — a
    /// previously-swallowed fault is now visible.
    /// </summary>
    private async Task SelectTaskAsync(TaskRowViewModel? task)
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

        try
        {
            ResetStages();

            // Lazily promote active flat tasks to the nested subfolder layout
            // so the convention spreads incrementally without a bulk migration.
            if (task.Task is { IsNested: false, IsArchived: false })
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
        catch (Exception ex)
        {
            // Surface the load fault through the VM's operation-error channel
            // (consistent with RunBusyAsync's catch).  The task completes
            // observed — no unobserved task exception escapes.
            StatusText = ex.Message;
        }
    }

    /// <summary>
    /// Delegate kept so the already-awaited caller in <c>Authoring.cs:66</c>
    /// (<c>await LoadSelectedTaskAsync(SelectedTask)</c>) works unchanged.
    /// </summary>
    private Task LoadSelectedTaskAsync(TaskRowViewModel? task) => SelectTaskAsync(task);

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
            RefreshStageDetail(null);
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
        RefreshStageDetail(stage);
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
