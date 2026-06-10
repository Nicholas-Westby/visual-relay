namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
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
        _runningStageNumber = stageNumber;
        _runningStageName = stageName;
        _runningStageNumbers[taskId] = stageNumber;
        _runningStageNames[taskId] = stageName;
        _runningTask = Tasks.FirstOrDefault(task => task.Id == taskId);
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    private void BeginRunningTask(TaskRowViewModel task)
    {
        _runningTaskIds.Add(task.Id);
        _runningTask = task;
        _runningTaskId = task.Id;
        _runningStageNumbers[task.Id] = null;
        _runningStageNames[task.Id] = null;
        _runStartedAt[task.Id] = DateTimeOffset.UtcNow;
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
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
            _runningStageNumber = stageNumber;
            _runningStageName = stageName;
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
            _runningTask = null;
            _runningTaskId = null;
            _runningStageNumber = null;
            _runningStageName = null;
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
                _runningTask = Tasks.FirstOrDefault(t => t.Id == task.Id);
            }
            // Do NOT mark other running tasks idle — during Phase 1 multiple
            // tasks plan concurrently, each owned by its own _runningTaskIds
            // entry. Only ClearRunningTask marks a specific task idle.
        }

        if (_runningTaskId is not null)
        {
            _runningTask = Tasks.FirstOrDefault(t => t.Id == _runningTaskId);
        }
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
