using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    private void HandleRelayEvent(RelayEvent relayEvent)
    {
        if (relayEvent.TaskId is { } taskId)
        {
            EventsFor(taskId).Insert(0, relayEvent);
        }

        var traceEntry = relayEvent.EventName == "trace" ? BuildTraceEntry(relayEvent) : null;
        if (traceEntry is not null && relayEvent.TaskId is { } traceTaskId)
        {
            TraceEntriesFor(traceTaskId).Insert(0, traceEntry);
        }

        UpdateRunningTask(relayEvent);
        if (relayEvent.TaskId != SelectedTask?.Id)
        {
            return;
        }

        _allTaskEvents.Insert(0, relayEvent);
        if (_selectedStageFilter is null || relayEvent.StageNumber == _selectedStageFilter)
        {
            Events.Insert(0, relayEvent);
        }

        if (traceEntry is not null)
        {
            _allTraceEntries.Insert(0, traceEntry);
            if (_selectedStageFilter is null || relayEvent.StageNumber == _selectedStageFilter)
            {
                TraceEntries.Insert(0, traceEntry);
            }
        }

        ApplyStageEventToBoard(relayEvent);
    }

    private void UpdateRunningTask(RelayEvent relayEvent)
    {
        if (relayEvent.StageNumber is { } stageNumber)
        {
            var stage = Stages.FirstOrDefault(s => s.Number == stageNumber);
            if (relayEvent.EventName == "stage_start" &&
                _runningTask is { } runningTask &&
                relayEvent.TaskId == runningTask.Id)
            {
                var stageName = relayEvent.Data is not null && relayEvent.Data.TryGetValue("name", out var name)
                    ? name
                    : stage?.Name;
                runningTask.MarkRunning(stageNumber, stageName ?? stage?.Name);
            }
        }
    }

    private void ApplyStageEventToBoard(RelayEvent relayEvent)
    {
        if (relayEvent.StageNumber is not { } stageNumber)
        {
            return;
        }

        var stage = Stages.FirstOrDefault(s => s.Number == stageNumber);
        if (stage is null)
        {
            return;
        }

        stage.Status = relayEvent.EventName switch
        {
            "stage_start" => "Running",
            "stage_done" or "stage_report" => "Done",
            "flagged" => "Flagged",
            _ => stage.Status
        };
        if (relayEvent.EventName is "stage_done" or "stage_report")
        {
            ApplyStageEventMetric(stage, relayEvent);
        }
    }

    private async Task RefreshTasksAfterDrainAsync()
    {
        await ReloadTaskListAsync();
        StatusText = StatusText == "Queue drained" ? FormatQueueStatus() : StatusText;
    }

    private async Task ReloadTaskListAsync(string? preferredTaskId = null)
    {
        var repository = new RelayTaskRepository(RootPath);
        Tasks.Clear();
        var tasks = ShowArchive ? await repository.ListCompletedAsync() : await repository.ListAsync();
        foreach (var task in tasks)
        {
            Tasks.Add(new TaskRowViewModel(task));
        }

        SelectedTask = preferredTaskId is null
            ? Tasks.FirstOrDefault()
            : Tasks.FirstOrDefault(task => task.Id == preferredTaskId) ?? Tasks.FirstOrDefault();
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

    private static TraceEntry? BuildTraceEntry(RelayEvent relayEvent)
    {
        if (relayEvent.Data is null ||
            !relayEvent.Data.TryGetValue("title", out var title) ||
            !relayEvent.Data.TryGetValue("content", out var content))
        {
            return null;
        }

        relayEvent.Data.TryGetValue("kind", out var kind);
        return new TraceEntry(ParseTraceKind(kind), title, content, relayEvent.StageNumber);
    }

    private static TraceEntryKind ParseTraceKind(string? kind) =>
        Enum.TryParse<TraceEntryKind>(kind, out var parsed) ? parsed : TraceEntryKind.AssistantText;

    private bool CanRefresh() => !IsBusy && Directory.Exists(RootPath);
    private bool CanToggleArchive() => Directory.Exists(RootPath);
    private bool CanRunSelected() => !IsBusy && !PauseRequested && SelectedTask is not null && !SelectedTask.IsArchived;
    private bool CanDrain() => !IsBusy && !PauseRequested && !ShowArchive && Tasks.Any(task => !task.NeedsReview);
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

    private void ClearLogState()
    {
        Events.Clear();
        TraceEntries.Clear();
        _allTaskEvents.Clear();
        _allTraceEntries.Clear();
    }

    private List<RelayEvent> EventsFor(string taskId)
    {
        if (!_liveEventsByTask.TryGetValue(taskId, out var events))
        {
            events = [];
            _liveEventsByTask[taskId] = events;
        }

        return events;
    }

    private List<TraceEntry> TraceEntriesFor(string taskId)
    {
        if (!_liveTraceEntriesByTask.TryGetValue(taskId, out var entries))
        {
            entries = [];
            _liveTraceEntriesByTask[taskId] = entries;
        }

        return entries;
    }

    private void NotifyPauseStateChanged()
    {
        OnPropertyChanged(nameof(PauseNoticeText));
        OnPropertyChanged(nameof(PauseButtonText));
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
