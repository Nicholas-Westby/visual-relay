using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class StageRowViewModelTests
{
    [Fact]
    public void StatusLabel_DoneWithDuration_ReadsCompletedInDuration()
    {
        // DurationLabel is set before Status so the "Done" setter sees a duration.
        var stage = new StageRowViewModel(RelayStages.All[8])
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
        var stage = new StageRowViewModel(RelayStages.All[8])
        {
            Status = "Done",
        };

        Assert.Equal("Complete", stage.StatusLabel);
    }

    [Fact]
    public void StatusLabel_NonDoneStatuses_AreUnchanged()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        Assert.Equal("Waiting", stage.StatusLabel);

        stage.Status = "Flagged";
        Assert.Equal("Flagged", stage.StatusLabel);
    }

    [Fact]
    public void StatusLabel_Running_ShowsLiveElapsed()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.MarkRunning(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(145));
        stage.RefreshElapsed(DateTimeOffset.UtcNow);

        Assert.Contains("2m 25s", stage.StatusLabel);
    }

    [Fact]
    public void MetricLabel_DoneStage_OmitsLeadingDurationKeepsCostTurnsTest()
    {
        var stage = new StageRowViewModel(RelayStages.All[8])
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
}
