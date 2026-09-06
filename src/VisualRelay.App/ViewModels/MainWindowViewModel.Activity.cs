using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

/// <summary>
/// Live activity bookkeeping the control API reports on <c>/state</c> so an
/// external driver can answer "is it stuck?", "what is it doing right now?" and
/// "what has it cost?" without reading files off disk. Every value here is
/// written on the UI thread from <c>HandleRelayEvent</c> and read on the UI
/// thread by the snapshot builder — no locking and no I/O.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// When the view model last handled ANY relay event (the event's own
    /// timestamp), or null when none has arrived since launch. A large
    /// now-minus-this gap while <see cref="IsBusy"/> is true means wedged.
    /// </summary>
    public DateTimeOffset? LastActivityUtc { get; private set; }

    /// <summary>
    /// The most recent relay event handled, or null until the first one arrives.
    /// </summary>
    public RelayEvent? LastRelayEvent { get; private set; }

    /// <summary>
    /// Cumulative USD accrued since launch, summed from the numeric per-stage
    /// <c>costUsd</c> the driver publishes on stage_done — the same accounting
    /// the driver keeps for one task's session total, continued across tasks.
    /// Zero until a priced stage completes.
    /// </summary>
    public double SessionCostUsd { get; private set; }

    /// <summary>
    /// One row per concurrently-running task, read from the same running-task
    /// tracking the queue rows use: the highest stage number the task has open,
    /// that stage's name, and the tier the stage board runs it at. Stage and
    /// tier are null while a running task has no stage open (between stages).
    /// Empty when nothing is running.
    /// </summary>
    public IReadOnlyList<RunningTaskSnapshot> RunningTasks()
    {
        var running = new List<RunningTaskSnapshot>(_runningTaskIds.Count);
        foreach (var taskId in _runningTaskIds)
        {
            var numbers = _runningStageNumbers.GetValueOrDefault(taskId);
            var stageNumber = numbers is { Count: > 0 } ? numbers.Max() : (int?)null;
            running.Add(new RunningTaskSnapshot(
                taskId,
                stageNumber,
                _runningStageNames.GetValueOrDefault(taskId),
                stageNumber is { } number ? Stages.FirstOrDefault(s => s.Number == number)?.Tier : null));
        }

        return running;
    }

    /// <summary>
    /// Records that a relay event was handled: the activity clock, the event
    /// itself, and any cost it carries. Called first thing in
    /// <c>HandleRelayEvent</c> so EVERY event counts as activity — including
    /// events for a task the user is not viewing.
    /// </summary>
    private void RecordActivity(RelayEvent relayEvent)
    {
        LastActivityUtc = relayEvent.Timestamp.ToUniversalTime();
        LastRelayEvent = relayEvent;

        if (relayEvent.EventName == "stage_done" && TryGetCostUsd(relayEvent) is { } costUsd)
        {
            SessionCostUsd += costUsd;
        }
    }
}

/// <summary>
/// A single running task as the control API reports it: which task, which stage
/// it currently has open (null between stages), that stage's name, and its tier.
/// </summary>
public sealed record RunningTaskSnapshot(
    string TaskId,
    int? StageNumber,
    string? StageName,
    string? Tier);
