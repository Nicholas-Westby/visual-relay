using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    private void HandleRelayEvent(RelayEvent relayEvent)
    {
        _allTaskEvents.Insert(0, relayEvent);
        if (_selectedStageFilter is null || relayEvent.StageNumber == _selectedStageFilter)
        {
            Events.Insert(0, relayEvent);
        }

        if (relayEvent.EventName == "trace")
        {
            AppendTraceEntry(relayEvent);
        }

        if (relayEvent.StageNumber is { } stageNumber)
        {
            var stage = Stages.FirstOrDefault(s => s.Number == stageNumber);
            if (stage is not null)
            {
                stage.Status = relayEvent.EventName switch
                {
                    "stage_start" => "Running",
                    "stage_done" => "Done",
                    "flagged" => "Flagged",
                    _ => stage.Status
                };
                if (relayEvent.EventName == "stage_done")
                {
                    ApplyStageEventMetric(stage, relayEvent);
                }
            }
        }
    }

    private async Task RefreshTasksAfterDrainAsync()
    {
        var repository = new RelayTaskRepository(RootPath);
        Tasks.Clear();
        foreach (var task in await repository.ListAsync())
        {
            Tasks.Add(task);
        }

        SelectedTask = Tasks.FirstOrDefault();
        StatusText = StatusText == "Queue drained" ? FormatQueueStatus() : StatusText;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetStages()
    {
        foreach (var stage in Stages)
        {
            stage.Status = "Waiting";
            stage.IsSelected = false;
            stage.ClearMetric();
        }
    }

    private string FormatQueueStatus()
    {
        var pending = Tasks.Count(task => !task.NeedsReview);
        var review = Tasks.Count(task => task.NeedsReview);
        return review == 0 ? $"{pending} pending" : $"{pending} pending · {review} review";
    }

    private void AppendTraceEntry(RelayEvent relayEvent)
    {
        if (relayEvent.Data is null ||
            !relayEvent.Data.TryGetValue("title", out var title) ||
            !relayEvent.Data.TryGetValue("content", out var content))
        {
            return;
        }

        relayEvent.Data.TryGetValue("kind", out var kind);
        var entry = new TraceEntry(ParseTraceKind(kind), title, content, relayEvent.StageNumber);
        _allTraceEntries.Insert(0, entry);
        if (_selectedStageFilter is null || relayEvent.StageNumber == _selectedStageFilter)
        {
            TraceEntries.Insert(0, entry);
        }
    }

    private static TraceEntryKind ParseTraceKind(string? kind) =>
        Enum.TryParse<TraceEntryKind>(kind, out var parsed) ? parsed : TraceEntryKind.AssistantText;

    private bool CanRefresh() => !IsBusy && Directory.Exists(RootPath);
    private bool CanRunSelected() => !IsBusy && SelectedTask is not null && !SelectedTask.IsArchived;
    private bool CanDrain() => !IsBusy && !ShowArchive && Tasks.Any(task => !task.NeedsReview);
    private bool HasSelection() => SelectedTask is not null && !IsBusy && !ShowArchive;

    private async Task LoadRunHistoryAsync(string taskId)
    {
        ClearLogState();
        var metric = RelayRunHistory.ReadTaskMetric(RootPath, taskId);
        SelectedTaskMetricLabel = metric.SummaryLabel;
        foreach (var stage in Stages)
        {
            stage.ClearMetric();
            var stageMetric = metric.Stages.FirstOrDefault(item => item.StageNumber == stage.Number);
            if (stageMetric is not null)
            {
                stage.ApplyMetric(stageMetric);
            }
        }

        _allTaskEvents.AddRange(RelayRunHistory.ReadTaskEvents(RootPath, taskId));
        _allTraceEntries.AddRange(await RelayRunHistory.ReadTraceEntriesAsync(RootPath, taskId));
        ApplyLogFilter();
    }

    private void ClearLogState()
    {
        Events.Clear();
        TraceEntries.Clear();
        _allTaskEvents.Clear();
        _allTraceEntries.Clear();
    }

    private void ApplyLogFilter()
    {
        Events.Clear();
        foreach (var relayEvent in _allTaskEvents.Where(IsInSelectedStage))
        {
            Events.Add(relayEvent);
        }

        TraceEntries.Clear();
        foreach (var entry in _allTraceEntries.Where(IsInSelectedStage))
        {
            TraceEntries.Add(entry);
        }
    }

    private bool IsInSelectedStage(RelayEvent relayEvent) =>
        _selectedStageFilter is null || relayEvent.StageNumber == _selectedStageFilter;

    private bool IsInSelectedStage(TraceEntry entry) =>
        _selectedStageFilter is null || entry.StageNumber == _selectedStageFilter;

    private static void ApplyStageEventMetric(StageRowViewModel stage, RelayEvent relayEvent)
    {
        if (relayEvent.Data is null)
        {
            return;
        }

        if (relayEvent.Data.TryGetValue("time", out var time))
        {
            stage.DurationLabel = time;
        }

        if (relayEvent.Data.TryGetValue("cost", out var cost))
        {
            stage.CostLabel = cost;
        }

        if (relayEvent.Data.TryGetValue("model", out var model))
        {
            stage.ModelLabel = model;
        }
    }
}
