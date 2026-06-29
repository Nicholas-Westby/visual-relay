using System.Globalization;
using System.Reflection;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A retried/escalated stage card (e.g. Fix-verify) must show TURNS and COST
/// summed across every attempt — like the elapsed time already is — and must
/// match the archived <see cref="VisualRelay.Core.Tasks.RelayRunHistory"/> squash
/// metric (which SUMS CostUsd + Turns). Locks the replay-clobber fix: the live
/// per-attempt stage_done replay (ApplyStageEventMetric) must ACCUMULATE turns +
/// cost, not clobber them to the latest attempt.
/// </summary>
[Collection("Headless")]
public sealed class StageCardCumulativeMetricsTests
{
    private static readonly MethodInfo ApplyStageEventMetricMethod = typeof(MainWindowViewModel)
        .GetMethod("ApplyStageEventMetric", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ApplyEvent(StageRowViewModel stage, RelayEvent relayEvent) =>
        ApplyStageEventMetricMethod.Invoke(null, [stage, relayEvent]);

    private static RelayEvent StageDone(int turns, double costUsd, double seconds) =>
        new(DateTimeOffset.UtcNow, "info", "stage_done", "run", "/root", "task", 10, "balanced",
            Data: new Dictionary<string, string>
            {
                ["name"] = "Fix-verify",
                ["time"] = $"{seconds:0}s",
                ["timeSeconds"] = seconds.ToString(CultureInfo.InvariantCulture),
                ["cost"] = MoneyFormatter.Dollars(costUsd),
                ["costUsd"] = costUsd.ToString(CultureInfo.InvariantCulture),
                ["turns"] = turns.ToString()
            });

    [AvaloniaFact]
    public void Turns_AreSummedAcrossAttempts_NotClobberedToLatest()
    {
        var stage = new StageRowViewModel(RelayStages.All[9]);

        ApplyEvent(stage, StageDone(turns: 4, costUsd: 0.01, seconds: 5));
        ApplyEvent(stage, StageDone(turns: 7, costUsd: 0.02, seconds: 10));

        Assert.Equal("11t", stage.TurnsLabel);
    }

    [AvaloniaFact]
    public void Cost_IsSummedAcrossAttempts_NotClobberedToLatest()
    {
        var stage = new StageRowViewModel(RelayStages.All[9]);

        ApplyEvent(stage, StageDone(turns: 4, costUsd: 0.01, seconds: 5));
        ApplyEvent(stage, StageDone(turns: 7, costUsd: 0.02, seconds: 10));

        Assert.Equal("$0.03", stage.CostLabel);
    }

    [AvaloniaFact]
    public void ReplayedLiveEvents_MatchArchivedSquashMetric()
    {
        // The archived squash sums per-attempt CostUsd + Turns + DurationSeconds.
        var squash = new StageRunMetric(
            StageNumber: 10, StageName: "Fix-verify", Tier: "balanced", Model: "claude",
            Timestamp: DateTimeOffset.UtcNow, DurationSeconds: 15, CostUsd: 0.03, Priced: true,
            PromptTokens: 0, CachedTokens: 0, OutputTokens: 0, CacheWriteTokens: 0,
            ReportPath: "/tmp/r.json", TraceDirectory: null, Turns: 11);

        // (a) The card built from the archived squash directly.
        var archived = new StageRowViewModel(RelayStages.All[9]);
        archived.ClearMetric();
        archived.ApplyMetric(squash);

        // (b) The card built by replaying the live per-attempt events (the
        //     task-switch path that used to CLOBBER turns/cost). Each attempt opens a
        //     segment (stage_start → MarkRunning) then banks on stage_done, exactly as
        //     ApplyStageEventToBoard drives it live.
        var replayed = new StageRowViewModel(RelayStages.All[9]);
        replayed.ClearMetric();
        replayed.MarkRunning(DateTimeOffset.UtcNow);
        ApplyEvent(replayed, StageDone(turns: 4, costUsd: 0.01, seconds: 5));
        replayed.MarkRunning(DateTimeOffset.UtcNow);
        ApplyEvent(replayed, StageDone(turns: 7, costUsd: 0.02, seconds: 10));

        Assert.Equal(archived.TurnsLabel, replayed.TurnsLabel);
        Assert.Equal(archived.CostLabel, replayed.CostLabel);
        Assert.Equal(archived.DurationLabel, replayed.DurationLabel);
        Assert.Equal("11t", replayed.TurnsLabel);
        Assert.Equal("$0.03", replayed.CostLabel);
        Assert.Equal("15s", replayed.DurationLabel);
    }

    [AvaloniaFact]
    public void ClearMetric_ResetsCumulativeTurnsAndCost()
    {
        var stage = new StageRowViewModel(RelayStages.All[9]);
        ApplyEvent(stage, StageDone(turns: 4, costUsd: 0.01, seconds: 5));

        stage.ClearMetric();
        ApplyEvent(stage, StageDone(turns: 7, costUsd: 0.02, seconds: 10));

        // After a reset the next attempt starts a fresh sum (7t / $0.02), not 11t.
        Assert.Equal("7t", stage.TurnsLabel);
        Assert.Equal("$0.02", stage.CostLabel);
    }
}
