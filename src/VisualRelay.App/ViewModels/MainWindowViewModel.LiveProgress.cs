namespace VisualRelay.App.ViewModels;

// ReSharper disable once UnusedType.Global — partial of MainWindowViewModel
public partial class MainWindowViewModel
{
    /// <summary>
    /// Authoritative live progress for a running task: the highest stage number
    /// completed this session, keyed by task id. It lives on the view-model rather
    /// than the row so it survives <c>ReloadTaskListAsync</c> (which throws rows
    /// away) and can be replayed onto a rebuilt row. On resume the driver never
    /// re-emits stage_done for stages it already finished, so the seed at
    /// <c>RestoreRunningTaskState</c>/<c>BeginRunningTask</c> establishes the base.
    /// </summary>
    private readonly Dictionary<string, int> _liveCompletedStageCounts = new(StringComparer.Ordinal);

    private int GetLiveCompletedStage(string taskId) =>
        _liveCompletedStageCounts.GetValueOrDefault(taskId);

    private void SetLiveCompletedStage(string taskId, int stageNumber) =>
        _liveCompletedStageCounts[taskId] = stageNumber;

    private void RemoveLiveCompletedStage(string taskId) =>
        _liveCompletedStageCounts.Remove(taskId);

    /// <summary>
    /// Raises the running task's live progress to <paramref name="stageNumber"/> and
    /// pushes the value onto the current row, if one exists. The dictionary advances
    /// even when the row was discarded mid-reload, so a later
    /// <c>ApplyRunningTaskToRows</c> can restore it.
    /// </summary>
    private void RecordLiveCompletedStage(string taskId, int stageNumber)
    {
        var value = Math.Max(GetLiveCompletedStage(taskId), stageNumber);
        _liveCompletedStageCounts[taskId] = value;
        if (Tasks.FirstOrDefault(t => t.Id == taskId) is { } task)
            task.SeedCompletedStageCount(value);
    }
}
