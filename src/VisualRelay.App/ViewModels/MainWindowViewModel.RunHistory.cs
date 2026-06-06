using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    private async Task LoadRunHistoryAsync(string taskId)
    {
        ClearLogState();
        var metric = RelayRunHistory.ReadTaskMetric(RootPath, taskId);
        SelectedTaskMetricLabel = metric.SummaryLabel;
        if (_runningTaskId != taskId)
        {
            SelectedTaskError = metric.Stages
                .Where(stage => !stage.Succeeded)
                .OrderByDescending(stage => stage.StageNumber)
                .Select(stage => stage.ErrorMessage)
                .FirstOrDefault();
        }
        foreach (var stage in Stages)
        {
            stage.ClearMetric();
            var stageMetric = metric.Stages.FirstOrDefault(item => item.StageNumber == stage.Number);
            if (stageMetric is not null)
            {
                stage.ApplyMetric(stageMetric);
            }
        }

        RevealStageArtifactsCommand.NotifyCanExecuteChanged();

        var liveEvents = EventsFor(taskId);
        _allTaskEvents.AddRange(liveEvents);
        _allTaskEvents.AddRange(RelayRunHistory.ReadTaskEvents(RootPath, taskId));
        _allTraceEntries.AddRange(TraceEntriesFor(taskId));
        _allTraceEntries.AddRange(await RelayRunHistory.ReadTraceEntriesAsync(RootPath, taskId));
        foreach (var relayEvent in liveEvents.OrderBy(item => item.Timestamp))
        {
            ApplyStageEventToBoard(relayEvent);
        }

        ApplyLogFilter();
    }
}
