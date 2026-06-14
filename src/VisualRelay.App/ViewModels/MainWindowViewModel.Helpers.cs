using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
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
                relayEvent.TaskId is { } taskId &&
                _runningTaskIds.Contains(taskId))
            {
                var stageName = relayEvent.Data is not null && relayEvent.Data.TryGetValue("name", out var name)
                    ? name
                    : stage?.Name;
                UpdateRunningStage(taskId, stageNumber, stageName ?? stage?.Name);
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

    private async Task RefreshTasksAfterDrainAsync(string? preferredTaskId = null)
    {
        await ReloadTaskListAsync(preferredTaskId);
        StatusText = StatusText == "Queue drained" ? FormatQueueStatus() : StatusText;
    }

    private async Task ReloadTaskListAsync(string? preferredTaskId = null)
    {
        var configResult = await RelayConfigLoader.TryLoadAsync(RootPath);
        NeedsInitialization = !ShowArchive && configResult.NeedsInitialization;
        ConfigDiagnostic = configResult.Status == RelayConfigStatus.Malformed ? configResult.Diagnostic : null;
        if (configResult.Status == RelayConfigStatus.Loaded)
        {
            BypassSandbox = configResult.Config.BypassSandbox;
            CommitProofArtifacts = configResult.Config.CommitProofArtifacts;
        }

        // IsNullOrEmpty (not WhiteSpace) so detection runs only when the user hasn't
        // touched the box yet; never clobbers a value the user has typed.
        if (NeedsInitialization && string.IsNullOrEmpty(InitTestCommandInput))
        {
            InitTestCommandInput = TestCommandDetector.Detect(RootPath);
        }

        var repository = new RelayTaskRepository(RootPath);
        Tasks.Clear();
        var tasks = ShowArchive ? await repository.ListCompletedAsync() : await repository.ListAsync();
        foreach (var task in tasks)
        {
            Tasks.Add(new TaskRowViewModel(task));
        }

        ApplyRunningTaskToRows();
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

    private bool CanMoveUp() =>
        HasSelection() && SelectedTask is { } selected && Tasks.IndexOf(selected) > 0;

    private bool CanMoveDown()
    {
        if (!HasSelection() || SelectedTask is not { } selected)
        {
            return false;
        }

        var index = Tasks.IndexOf(selected);
        return index >= 0 && index < Tasks.Count - 1;
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
        if (relayEvent.Data.TryGetValue("turns", out var turns))
        {
            stage.TurnsLabel = turns + "t";
        }
    }
}
