using System.Globalization;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

// Elapsed/metric accounting derived from a task's stage events: per-STAGE metric
// application (duration/cost/turns onto the board card) and the per-TASK overall
// ACTIVE-time accumulator. Both bank the same per-attempt reported durations, so a
// retried stage's card and the queue-row overall reconcile; the overall excludes
// idle queue-wait because no segment is open while a task is parked.
public partial class MainWindowViewModel
{
    // Per-task overall active time = the sum of the task's own stage segments
    // (excludes the idle queue-wait a task spends parked while another task
    // executes during a drain). Keyed by task id; created at plan/run start and
    // dropped when the run ends. Drives the queue-row "overall" elapsed so it
    // reconciles with the per-stage cards instead of dwarfing them.
    private readonly Dictionary<string, CumulativeElapsed> _taskElapsed = new(StringComparer.Ordinal);

    private static void ApplyStageEventMetric(StageRowViewModel stage, RelayEvent relayEvent)
    {
        if (relayEvent.Data is null)
        {
            return;
        }

        // Bank this attempt's reported duration so a retried stage's recorded
        // duration is the SUM across attempts. Fall back to the formatted "time"
        // (no accumulation) only for legacy events lacking the numeric value.
        if (TryGetReportedDuration(relayEvent) is { } reported)
            stage.AccumulateCompletedDuration(reported);
        else if (relayEvent.Data.TryGetValue("time", out var time))
            stage.DurationLabel = time;
        // Bank this attempt's cost + turns the SAME way (cumulative across attempts)
        // so the retried card matches the archived squash. The numeric "costUsd" is
        // the accumulation source; fall back to the formatted "cost" string (e.g. "?")
        // only when it is absent (an unpriced attempt).
        if (TryGetCostUsd(relayEvent) is { } costUsd)
            stage.AccumulateCost(costUsd);
        else if (relayEvent.Data.TryGetValue("cost", out var cost))
            stage.CostLabel = cost;
        if (relayEvent.Data.TryGetValue("model", out var model))
            stage.ModelLabel = model;
        if (relayEvent.Data.TryGetValue("turns", out var turns) && int.TryParse(turns, out var turnCount))
        {
            stage.AccumulateTurns(turnCount);
        }
        if (relayEvent.Data.TryGetValue("testTime", out var testTime))
        {
            stage.TestDurationLabel = testTime;
        }
    }

    /// <summary>
    /// Drives the per-task overall ACTIVE-time accumulator from the task's stage
    /// events. A stage_start opens a segment; stage_done/stage_report banks the
    /// attempt's reported duration; while no segment is open (the idle queue-wait),
    /// nothing accrues — so the overall is the sum of the task's stage segments and
    /// excludes the parked wait. Mirrors the per-stage card accumulation so the
    /// queue-row overall and the stage cards reconcile.
    /// </summary>
    private void AccumulateTaskActiveTime(RelayEvent relayEvent)
    {
        if (relayEvent.TaskId is not { } taskId ||
            relayEvent.StageNumber is null ||
            !_taskElapsed.TryGetValue(taskId, out var elapsed))
        {
            return;
        }

        switch (relayEvent.EventName)
        {
            case "stage_start":
                elapsed.StartSegment(relayEvent.Timestamp);
                break;
            case "stage_done":
            case "stage_report":
                if (TryGetReportedDuration(relayEvent) is { } reported)
                    elapsed.CompleteSegment(reported);
                else
                    elapsed.CompleteSegment(relayEvent.Timestamp);
                break;
            case "flagged":
                elapsed.StopSegment();
                break;
        }
    }

    /// <summary>
    /// Reads the numeric per-attempt stage duration ("timeSeconds") emitted on
    /// stage_done/stage_report. Null when absent or non-positive.
    /// </summary>
    private static TimeSpan? TryGetReportedDuration(RelayEvent relayEvent) =>
        relayEvent.Data is not null &&
        relayEvent.Data.TryGetValue("timeSeconds", out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>
    /// Reads the numeric per-attempt stage cost ("costUsd") emitted on stage_done —
    /// the machine-readable companion to the formatted "cost" label, so the GUI can
    /// SUM per-attempt cost for a retried stage. Null when absent (an unpriced
    /// attempt, which falls back to the formatted "cost" string, e.g. "?").
    /// </summary>
    private static double? TryGetCostUsd(RelayEvent relayEvent) =>
        relayEvent.Data is not null &&
        relayEvent.Data.TryGetValue("costUsd", out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var usd)
            ? usd
            : null;
}
