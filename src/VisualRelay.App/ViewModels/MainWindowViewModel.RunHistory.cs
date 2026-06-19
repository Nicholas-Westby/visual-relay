using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // Single source of truth for the detail-pane error: the highest-stage
    // "Flagged" entry in the driver-written status record (null when none).
    // Shared by the selection-load path and the run-completion refresh so the
    // banner can never drift between them.
    private static string? LatestFlaggedError(IReadOnlyList<StageStatusEntry> statusRecord) =>
        statusRecord
            .Where(e => e.Status == "Flagged")
            .OrderByDescending(e => e.Stage)
            .Select(e => e.Error)
            .FirstOrDefault();

    private async Task LoadRunHistoryAsync(string taskId)
    {
        ClearLogState();
        var metric = RelayRunHistory.ReadTaskMetric(RootPath, taskId);
        SelectedTaskMetricLabel = metric.SummaryLabel;

        // Status comes from the driver-written record, not from report-derived metrics.
        var statusRecord = RelayRunHistory.ReadStatusRecord(RootPath, taskId);
        if (_runningTaskId != taskId)
        {
            SelectedTaskError = LatestFlaggedError(statusRecord);
        }

        foreach (var stage in Stages)
        {
            stage.ClearMetric();
            var statusEntry = statusRecord.FirstOrDefault(e => e.Stage == stage.Number);
            if (statusEntry is not null)
            {
                stage.Status = statusEntry.Status;
                stage.SetTestDurationSeconds(statusEntry.TestDurationSeconds);
            }

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
