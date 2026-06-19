using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // internal so a VM test can drive the drain run-start / completion hooks
    // directly (clearing + refreshing the detail-pane error) without launching
    // a real swival/relay run. The drain command builds it the same way.
    internal DrainLifecycleCallbacks CreateDrainLifecycleCallbacks()
    {
        return new DrainLifecycleCallbacks
        {
            OnPlanningStarted = taskId =>
            {
                StatusText = $"Planning {taskId}…";
                Tasks.FirstOrDefault(t => t.Id == taskId)?.MarkPlanning();
            },
            OnPlanningCompleted = (taskId, status) =>
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    if (status == RelayTaskOutcomeStatus.Flagged)
                        task.MarkIdle();
                    else
                        task.MarkPlanned();
                }
            },
            OnExecuteStarted = taskId =>
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                    BeginRunningTask(task);
            },
            OnExecuteCompleted = (taskId, _) =>
            {
                ClearRunningTask(taskId);
                // After clearing _runningTaskId, refresh the detail-pane error
                // when this is the viewed task: flag ⇒ new reason, commit ⇒ stays
                // cleared. Fixes the stale "LATEST RUN FAILED" banner persisting
                // across a drain re-run of the selected task.
                RefreshSelectedTaskErrorAfterRun(taskId);
            }
        };
    }

    internal void RestoreRunningTaskState(string taskId, int? stageNumber, string? stageName)
    {
        // Clear all previous running tasks — restore replaces the entire set.
        var snapshot = new List<string>(_runningTaskIds);
        foreach (var id in snapshot)
        {
            var t = Tasks.FirstOrDefault(task => task.Id == id);
            if (t is not null)
                t.MarkIdle();
        }

        _runningTaskIds.Clear();
        _runningTaskIds.Add(taskId);
        _runningTaskId = taskId;
        _runningStageNumbers[taskId] = stageNumber;
        _runningStageNames[taskId] = stageName;
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    private void BeginRunningTask(TaskRowViewModel task)
    {
        _runningTaskIds.Add(task.Id);
        _runningTaskId = task.Id;
        _runningStageNumbers[task.Id] = null;
        _runningStageNames[task.Id] = null;
        _runStartedAt[task.Id] = DateTimeOffset.UtcNow;
        ClearSelectedTaskErrorForRunStart(task.Id);
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    /// <summary>
    /// On run start, drop the stale "LATEST RUN FAILED" banner when the task
    /// being (re-)run is the one shown in the detail pane — the prior run's
    /// error no longer describes the current run. Other tasks' displayed error
    /// is untouched. Shared by both the single-run path (RunOneAsync) and the
    /// queue drain (OnExecuteStarted) since both route run start through
    /// <see cref="BeginRunningTask"/>. Runs on the UI thread (its only callers
    /// already mutate ObservableProperty state from the UI thread).
    /// </summary>
    private void ClearSelectedTaskErrorForRunStart(string startingTaskId)
    {
        if (string.Equals(startingTaskId, SelectedTask?.Id, StringComparison.Ordinal))
        {
            SelectedTaskError = null;
        }
    }

    private void UpdateRunningStage(string taskId, int stageNumber, string? stageName)
    {
        if (!_runningTaskIds.Contains(taskId))
            return;

        _runningStageNumbers[taskId] = stageNumber;
        _runningStageNames[taskId] = stageName;

        // Update the task row directly — ApplyRunningTaskToRows refreshes all
        // running rows, but we want an immediate update for this specific task.
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is not null)
        {
            task.MarkRunning(stageNumber, stageName);
        }

        // If this is the followed task, update detail pane context too.
        if (string.Equals(taskId, _runningTaskId, StringComparison.Ordinal))
        {
            NotifyRunningTaskContextChanged();
        }
    }

    private void ClearRunningTask(string taskId)
    {
        _runningTaskIds.Remove(taskId);
        _runningStageNumbers.Remove(taskId);
        _runningStageNames.Remove(taskId);
        _runStartedAt.Remove(taskId);

        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is not null)
            task.MarkIdle();

        if (string.Equals(_runningTaskId, taskId, StringComparison.Ordinal))
        {
            _runningTaskId = null;
        }

        NotifyRunningTaskContextChanged();
    }

    private void ApplyRunningTaskToRows()
    {
        foreach (var task in Tasks)
        {
            if (_runningTaskIds.Contains(task.Id))
            {
                _runningStageNumbers.TryGetValue(task.Id, out var stageNum);
                _runningStageNames.TryGetValue(task.Id, out var stageName);
                task.MarkRunning(stageNum, stageName);
            }
            // Do NOT mark other running tasks idle — during Phase 1 multiple
            // tasks plan concurrently, each owned by its own _runningTaskIds
            // entry. Only ClearRunningTask marks a specific task idle.
        }
    }

    /// <summary>
    /// Completion-refresh handle, captured so tests can deterministically
    /// <c>await viewModel.LastRunCompletionRefresh</c> instead of polling. The
    /// refresh itself is synchronous (a status-record read); the task is a
    /// settled marker. Null until the first run completes for the selected task.
    /// </summary>
    internal Task? LastRunCompletionRefresh { get; private set; }

    /// <summary>
    /// On run completion, refresh the detail-pane error for the just-finished
    /// task when it is the one on screen: a flag surfaces the new reason, a
    /// commit leaves it cleared (no flagged entry in the freshly-written status
    /// record). Re-reads only the status record — not the full run history — so
    /// it never disturbs the live log/board view. Runs on the UI thread (its
    /// callers already mutate ObservableProperty state from the UI thread).
    /// </summary>
    private void RefreshSelectedTaskErrorAfterRun(string completedTaskId)
    {
        if (string.Equals(completedTaskId, SelectedTask?.Id, StringComparison.Ordinal))
        {
            var statusRecord = Core.Tasks.RelayRunHistory.ReadStatusRecord(RootPath, completedTaskId);
            SelectedTaskError = LatestFlaggedError(statusRecord);
        }

        LastRunCompletionRefresh = Task.CompletedTask;
    }

    private bool CanFollowRunningTask() =>
        _runningTaskId is not null &&
        (SelectedTask is null || !string.Equals(SelectedTask.Id, _runningTaskId, StringComparison.Ordinal));

    private void NotifyRunningTaskContextChanged()
    {
        OnPropertyChanged(nameof(IsViewingDifferentTaskDuringRun));
        OnPropertyChanged(nameof(ViewingRunContextText));
        OnPropertyChanged(nameof(PauseNoticeText));
        FollowRunningTaskCommand.NotifyCanExecuteChanged();
    }
}
