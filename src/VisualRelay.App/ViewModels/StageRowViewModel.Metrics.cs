using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

// Per-attempt metric BANKING for a retried/escalated stage card. TIME, TURNS, and
// COST are all accumulated across the stage's attempts (mirroring the cumulative
// elapsed shape — turns is a plain int sum, cost a double sum) so the live card
// reads the SUM and matches the archived RelayRunHistory.SquashAttempts metric,
// rather than clobbering to the latest attempt on each live stage_done replay.
public sealed partial class StageRowViewModel
{
    // Cumulative across attempts. Mirrors the _elapsed (CumulativeElapsed) shape:
    // a retry adds to the running total instead of replacing it.
    private int _turnsTotal;
    private double _costTotal;

    /// <summary>
    /// Banks a completed attempt's reported duration and shows the cumulative total
    /// as the stage's recorded duration. Called on a live stage_done so a retried
    /// stage's finished card reads the SUM across attempts, not just the last one.
    /// </summary>
    public void AccumulateCompletedDuration(TimeSpan reportedDuration)
    {
        _elapsed.CompleteSegment(reportedDuration);
        DurationLabel = ElapsedFormatter.Label(_elapsed.Completed);
    }

    /// <summary>
    /// Adds this attempt's turn count to the running stage total and shows the SUM,
    /// so a retried/escalated stage's card reads cumulative turns (e.g. 200 + 400).
    /// </summary>
    public void AccumulateTurns(int turns)
    {
        _turnsTotal += turns;
        TurnsLabel = _turnsTotal > 0 ? $"{_turnsTotal}t" : string.Empty;
    }

    /// <summary>
    /// Adds this attempt's cost to the running stage total and shows the SUM, so a
    /// retried/escalated stage's card reads cumulative cost matching the squash.
    /// </summary>
    public void AccumulateCost(double costUsd)
    {
        _costTotal += costUsd;
        CostLabel = MoneyFormatter.Dollars(_costTotal);
    }

    public void ApplyMetric(StageRunMetric metric)
    {
        DurationLabel = metric.DurationLabel;
        CostLabel = metric.CostLabel;
        ModelLabel = metric.Model;
        TurnsLabel = metric.Turns > 0 ? $"{metric.Turns}t" : string.Empty;
        ReportPath = metric.ReportPath;
        TraceDirectory = metric.TraceDirectory;
    }

    public void ClearMetric()
    {
        DurationLabel = "No run yet";
        CostLabel = "No cost yet";
        ModelLabel = string.Empty;
        TurnsLabel = string.Empty;
        TestDurationLabel = string.Empty;
        ReportPath = null;
        TraceDirectory = null;
        _elapsed.Reset();
        _turnsTotal = 0;
        _costTotal = 0;
        ElapsedLabel = string.Empty;
    }
}
