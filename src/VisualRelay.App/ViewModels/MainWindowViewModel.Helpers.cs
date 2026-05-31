using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    private void HandleRelayEvent(RelayEvent relayEvent)
    {
        Events.Insert(0, relayEvent);
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
        TraceEntries.Insert(0, new TraceEntry(ParseTraceKind(kind), title, content));
    }

    private static TraceEntryKind ParseTraceKind(string? kind) =>
        Enum.TryParse<TraceEntryKind>(kind, out var parsed) ? parsed : TraceEntryKind.AssistantText;

    private bool CanRefresh() => !IsBusy && Directory.Exists(RootPath);
    private bool CanRunSelected() => !IsBusy && SelectedTask is not null;
    private bool CanDrain() => !IsBusy && Tasks.Any(task => !task.NeedsReview);
    private bool HasSelection() => SelectedTask is not null && !IsBusy;
}
