using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class StageRowViewModelTests
{
    [Fact]
    public void StatusLabel_DoneWithDuration_ReadsCompletedInDuration()
    {
        // DurationLabel is set before Status so the "Done" setter sees a duration.
        var stage = new StageRowViewModel(RelayStages.All[9])
        {
            DurationLabel = "17s",
            Status = "Done",
        };

        Assert.Equal("Completed in 17s", stage.StatusLabel);
    }

    [Fact]
    public void StatusLabel_DoneWithoutDuration_StaysComplete()
    {
        // No duration recorded — DurationLabel is still the "No run yet" sentinel.
        var stage = new StageRowViewModel(RelayStages.All[9])
        {
            Status = "Done",
        };

        Assert.Equal("Complete", stage.StatusLabel);
    }

    [Fact]
    public void StatusLabel_NonDoneStatuses_AreUnchanged()
    {
        var stage = new StageRowViewModel(RelayStages.All[9]);
        Assert.Equal("Waiting", stage.StatusLabel);

        stage.Status = "Flagged";
        Assert.Equal("Flagged", stage.StatusLabel);
    }

    [Fact]
    public void StatusLabel_Running_ShowsLiveElapsed()
    {
        var stage = new StageRowViewModel(RelayStages.All[9]);
        stage.MarkRunning(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(145));
        stage.RefreshElapsed(DateTimeOffset.UtcNow);

        Assert.Contains("2m 25s", stage.StatusLabel);
    }

    [Fact]
    public void MetricLabel_DoneStage_OmitsLeadingDurationKeepsCostTurnsTest()
    {
        var stage = new StageRowViewModel(RelayStages.All[9])
        {
            DurationLabel = "17s",
            CostLabel = "$0.0029",
            TurnsLabel = "4t",
            TestDurationLabel = "7s",
            Status = "Done",
        };

        // The duration moved to the status row; the metrics line no longer
        // leads with it but still carries cost + turns + test.
        Assert.DoesNotContain("17s", stage.MetricLabel);
        Assert.Contains("$0.0029", stage.MetricLabel);
        Assert.Contains("4t", stage.MetricLabel);
        Assert.Contains("test 7s", stage.MetricLabel);
    }

    [Fact]
    public void Constructor_StoresStageProperties()
    {
        var stage = new StageRowViewModel(RelayStages.All[0]);

        Assert.Equal(1, stage.Number);
        Assert.Equal("Ideate", stage.Name);
        Assert.Equal("cheap", stage.Tier);
        Assert.Equal("Waiting", stage.Status);
    }

    [Fact]
    public void SelectCommand_DefaultsToNull()
    {
        var stage = new StageRowViewModel(RelayStages.All[0]);

        Assert.Null(stage.SelectCommand);
    }

    [Fact]
    public void SelectCommand_StoresCommandWhenPassed()
    {
        var command = new RelayCommand<StageRowViewModel>(_ => { });
        var stage = new StageRowViewModel(RelayStages.All[0], command);

        Assert.Same(command, stage.SelectCommand);
    }

    [Fact]
    public void SelectCommand_CanBeNull()
    {
        var stage = new StageRowViewModel(RelayStages.All[0]);

        Assert.Null(stage.SelectCommand);
    }

    [Fact]
    public void SelectCommand_IsIRelayCommandOfStageRowViewModel()
    {
        var command = new RelayCommand<StageRowViewModel>(_ => { });
        var stage = new StageRowViewModel(RelayStages.All[0], command);

        var selectCommand = stage.SelectCommand;

        Assert.NotNull(selectCommand);
        Assert.IsAssignableFrom<IRelayCommand<StageRowViewModel>>(selectCommand);
    }

    [Fact]
    public void TierLabel_NoRun_ShowsDefinitionTier()
    {
        // Stage 7 Review runs on frontier. Before any run the card must still
        // show its definition tier.
        var stage = new StageRowViewModel(RelayStages.All[6]);

        Assert.Equal("frontier", stage.TierLabel);
    }

    [Fact]
    public void TierLabel_RunRecordingSameTier_ShowsTierOnly()
    {
        // Stage 1 Ideate runs on cheap. A run that records "cheap" as the model
        // must not produce "cheap cheap" — the model adds no information here.
        var stage = new StageRowViewModel(RelayStages.All[0]);
        stage.ApplyMetric(MetricFor(tier: "cheap", model: "cheap"));

        Assert.Equal("cheap", stage.TierLabel);
    }

    [Fact]
    public void TierLabel_RunRecordingEscalatedTier_ShowsRecordedTier()
    {
        // Stage 11 Fix-verify is defined as balanced, but escalation can bump it
        // to frontier mid-run. The label must prefer the tier that actually ran.
        var stage = new StageRowViewModel(RelayStages.All[10]);
        stage.ApplyMetric(MetricFor(tier: "frontier", model: string.Empty));

        Assert.Equal("frontier", stage.TierLabel);
    }

    [Fact]
    public void TierLabel_RunRecordingDistinctModel_ShowsTierAndModel()
    {
        // A frontier stage that recorded a concrete model distinct from the tier
        // shows both, joined by a separator that survives the 165 px card.
        var stage = new StageRowViewModel(RelayStages.All[6]);
        stage.ApplyMetric(MetricFor(tier: "frontier", model: "glm-5.3"));

        Assert.Equal("frontier · glm-5.3", stage.TierLabel);
    }

    [Fact]
    public void TierLabel_ClearMetric_RevertsToDefinitionTier()
    {
        var stage = new StageRowViewModel(RelayStages.All[6]);
        stage.ApplyMetric(MetricFor(tier: "vision", model: "glm-5.3"));

        stage.ClearMetric();

        Assert.Equal("frontier", stage.TierLabel);
    }

    private static StageRunMetric MetricFor(string tier, string model) =>
        new(
            StageNumber: 1,
            StageName: "Test",
            Tier: tier,
            Model: model,
            Timestamp: DateTimeOffset.UtcNow,
            DurationSeconds: 1,
            CostUsd: 0,
            Priced: false,
            PromptTokens: 0,
            CachedTokens: 0,
            OutputTokens: 0,
            CacheWriteTokens: 0,
            ReportPath: "/tmp/r.md",
            TraceDirectory: null,
            Turns: 0);
}
