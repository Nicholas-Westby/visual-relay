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
                // Fresh active-time accumulator for this run; planning stages (1–4)
                // accrue into it as their stage_done events arrive.
                _taskElapsed[taskId] = new CumulativeElapsed();
                Tasks.FirstOrDefault(t => t.Id == taskId)?.MarkPlanning();
            },
            OnPlanningCompleted = (taskId, outcome) =>
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                    {
                        // Live-update the row's backing record so NeedsReview/StateLabel
                        // reflect immediately while the drain is still running.
                        task.UpdateTask(task.Task with { ReviewReason = outcome.Reason ?? "Needs review" });
                        _taskElapsed.Remove(taskId);
                        task.MarkIdle();
                        // Also refresh the detail-pane error for the selected task when
                        // it flagged during planning (previously this was only done in
                        // OnExecuteCompleted, leaving planning-phase flags stale).
                        RefreshSelectedTaskErrorAfterRun(taskId);
                    }
                    else
                        task.MarkPlanned();
                }
            },
            OnExecuteStarted = taskId =>
            {
                // The execute phase must OWN the status text — otherwise it stays on the
                // last concurrent-planning message ("Planning <last task>…") for the whole
                // run, even though a different (earlier) task is executing. Mirror the
                // single-run path (RunOneAsync sets "Running <id>").
                StatusText = $"Running {taskId}";
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                    BeginRunningTask(task);
            },
            OnExecuteCompleted = (taskId, outcome) =>
            {
                ClearRunningTask(taskId);
                // Publish the FULL outcome — the drain has the real commit SHA and
                // flag reason, so the summary matches the single-run path (which also
                // forwards the full outcome) instead of showing blank commit/reason.
                _ = ExportSummaryOnCompletion(taskId, outcome);
                // After clearing _runningTaskId, refresh the detail-pane error
                // when this is the viewed task: flag ⇒ new reason, commit ⇒ stays
                // cleared. Fixes the stale "LATEST RUN FAILED" banner persisting
                // across a drain re-run of the selected task.
                RefreshSelectedTaskErrorAfterRun(taskId);
                // Reconcile the live roster with the on-disk archive: a committed task
                // whose spec moved to completed/ must leave the active list (else it
                // lingers as "Pending" and its stale MarkdownPath is re-read).
                ReconcileArchivedTaskRow(taskId);

                // Live-update the GUI row's backing record when the task flagged so
                // NeedsReview / StateLabel reflect immediately while the drain is
                // still running (the controller already wrote NEEDS-REVIEW to disk).
                if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                {
                    var row = Tasks.FirstOrDefault(t => t.Id == taskId);
                    if (row is not null)
                        row.UpdateTask(row.Task with { ReviewReason = outcome.Reason ?? "Needs review" });
                }
            }
        };
    }

    /// <summary>
    /// When a committed task's spec was moved to <c>completed/</c>, the row's
    /// <c>MarkdownPath</c> becomes stale, causing "Pending" to persist and detail
    /// re-reads to fail. Detect the move and drop the row; flag/archiveOnDone-off
    /// tasks keep their spec → no-op. Runs on the UI thread.
    /// </summary>
    private void ReconcileArchivedTaskRow(string taskId)
    {
        var row = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (row is null || File.Exists(row.MarkdownPath))
            return;
        if (string.Equals(SelectedTask?.Id, taskId, StringComparison.Ordinal))
            SelectedTask = Tasks.FirstOrDefault(t => !string.Equals(t.Id, taskId, StringComparison.Ordinal));
        Tasks.Remove(row);
        DrainQueueCommand.NotifyCanExecuteChanged();
    }

    internal void RestoreRunningTaskState(string taskId, int? stageNumber, string? stageName)
    {
        var snapshot = new List<string>(_runningTaskIds);
        foreach (var id in snapshot)
        {
            if (Tasks.FirstOrDefault(task => task.Id == id) is { } t)
                t.MarkIdle();
        }
        _runningTaskIds.Clear();
        _runningTaskIds.Add(taskId);
        _runningTaskId = taskId;
        _runningStageNumbers[taskId] = stageNumber;
        _runningStageNames[taskId] = stageName;
        _taskElapsed.TryAdd(taskId, new CumulativeElapsed());
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    private void BeginRunningTask(TaskRowViewModel task)
    {
        _runningTaskIds.Add(task.Id);
        _runningTaskId = task.Id;
        _runningStageNumbers[task.Id] = null;
        _runningStageNames[task.Id] = null;
        // TryAdd preserves a planning-phase accumulator seeded by OnPlanningStarted
        // (so the overall keeps the planning-stage active time across the
        // plan→execute boundary); it creates a fresh one for the single-run path
        // and for a resume where planning was already done.
        _taskElapsed.TryAdd(task.Id, new CumulativeElapsed());
        _rewriteUndo.Discard(task.Id);
        RaiseRewriteStateChanged();
        ClearSelectedTaskErrorForRunStart(task.Id);
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
        MarkSelectedTaskDoneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// On run start, clear the stale "LATEST RUN FAILED" banner for the
    /// (re-)running task when it is the selected one. Shared by single-run
    /// (RunOneAsync) and queue drain (OnExecuteStarted) paths. UI-thread safe.
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
        _taskElapsed.Remove(taskId);

        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is not null)
            task.MarkIdle();

        if (string.Equals(_runningTaskId, taskId, StringComparison.Ordinal))
        {
            _runningTaskId = null;
        }

        NotifyRunningTaskContextChanged();
        MarkSelectedTaskDoneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One per-second refresh of every "elapsed while running" label — running
    /// task rows plus the active stage card. Extracted from the 1-second
    /// DispatcherTimer's tick so tests can seed a past start and call it directly
    /// (no real wall-clock wait). Runs on the UI thread (the timer already ticks
    /// there). Cost is a handful of label assignments per second.
    /// </summary>
    public void UpdateRunningElapsedLabels()
    {
        var now = DateTimeOffset.UtcNow;

        // Update the overall elapsed for every currently-running task row: the
        // task's own active time (sum of its stage segments + the live stage),
        // NOT the wall-clock since planning — so it reconciles with the stage cards
        // and excludes idle queue-wait.
        foreach (var taskId in _runningTaskIds)
        {
            if (_taskElapsed.TryGetValue(taskId, out var elapsed))
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                    task.RunningElapsedLabel = ElapsedFormatter.Label(elapsed.Total(now));
            }
        }

        // Update elapsed for every currently-rewriting task row.
        foreach (var taskId in _rewritingTaskIds)
        {
            if (_rewriteStartedAt.TryGetValue(taskId, out var startedAt))
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                    task.RewriteElapsedLabel = ElapsedFormatter.Label(now - startedAt);
            }
        }

        // Let the toolbar stopwatch binding see the latest elapsed every tick.
        OnPropertyChanged(nameof(SelectedTaskRewriteElapsed));

        // Update the active stage card's elapsed (each running stage tracks its
        // own start, set on stage_start; non-running stages are a no-op).
        foreach (var stage in Stages)
        {
            stage.RefreshElapsed(now);
        }
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
    /// <c>await viewModel.LastRunCompletionRefresh</c> instead of polling.
    /// Null until the first run completes for the selected task.
    /// </summary>
    internal Task? LastRunCompletionRefresh { get; private set; }

    /// <summary>
    /// On run completion, refresh the detail-pane error for the just-finished
    /// task if it is on screen: a flag surfaces the new reason, a commit leaves
    /// it cleared. Re-reads only the status record — never the full run history.
    /// UI-thread safe.
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
        AddAttachmentsCommand.NotifyCanExecuteChanged();
    }

}
