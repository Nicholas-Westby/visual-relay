namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    internal void RestoreRunningTaskState(string taskId, int? stageNumber, string? stageName)
    {
        _runningTaskId = taskId;
        _runningStageNumber = stageNumber;
        _runningStageName = stageName;
        _runningTask = Tasks.FirstOrDefault(task => task.Id == taskId);
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    private void BeginRunningTask(TaskRowViewModel task)
    {
        _runningTask = task;
        _runningTaskId = task.Id;
        _runningStageNumber = null;
        _runningStageName = null;
        _runStartedAt = DateTimeOffset.UtcNow;
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    private void UpdateRunningStage(string taskId, int stageNumber, string? stageName)
    {
        if (!string.Equals(taskId, _runningTaskId, StringComparison.Ordinal))
        {
            return;
        }

        _runningStageNumber = stageNumber;
        _runningStageName = stageName;
        ApplyRunningTaskToRows();
        NotifyRunningTaskContextChanged();
    }

    private void ClearRunningTask(string taskId)
    {
        if (_runningTask is { } runningTask && string.Equals(runningTask.Id, taskId, StringComparison.Ordinal))
        {
            runningTask.MarkIdle();
        }

        foreach (var task in Tasks.Where(task => string.Equals(task.Id, taskId, StringComparison.Ordinal)))
        {
            task.MarkIdle();
        }

        if (string.Equals(_runningTaskId, taskId, StringComparison.Ordinal))
        {
            _runningTask = null;
            _runningTaskId = null;
            _runningStageNumber = null;
            _runningStageName = null;
            _runStartedAt = null;
        }

        NotifyRunningTaskContextChanged();
    }

    private void ApplyRunningTaskToRows()
    {
        foreach (var task in Tasks)
        {
            if (_runningTaskId is not null && string.Equals(task.Id, _runningTaskId, StringComparison.Ordinal))
            {
                task.MarkRunning(_runningStageNumber, _runningStageName);
                _runningTask = task;
            }
            else if (task.IsRunning)
            {
                task.MarkIdle();
            }
        }

        if (_runningTask is not null && _runningTaskId is not null)
        {
            _runningTask.MarkRunning(_runningStageNumber, _runningStageName);
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
