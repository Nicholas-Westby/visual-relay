using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class TaskRowViewModelTests
{
    private static double StageFraction(int stage) =>
        stage / (double)RelayStages.All.Count;

    [Fact]
    public void SeedCompletedStageCount_RunningRow_ReadsSeededFraction()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();
        row.SeedCompletedStageCount(6);

        Assert.Equal(StageFraction(6), row.ProgressFraction, precision: 6);
    }

    [Fact]
    public void RecordStageCompleted_HighStageNumber_ReadsThatStageNotOne()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();
        row.RecordStageCompleted(10);

        Assert.Equal(StageFraction(10), row.ProgressFraction, precision: 6);
    }

    [Fact]
    public void RecordStageCompleted_LowerStageNumber_DoesNotMoveBackwards()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();
        row.RecordStageCompleted(8);
        row.RecordStageCompleted(3);

        Assert.Equal(StageFraction(8), row.ProgressFraction, precision: 6);
    }

    [Fact]
    public void RecordStageCompleted_SameStageTwice_DoesNotDoubleCount()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();
        row.RecordStageCompleted(4);
        row.RecordStageCompleted(4);

        Assert.Equal(StageFraction(4), row.ProgressFraction, precision: 6);
    }

    [Fact]
    public void MarkIdle_AfterLiveProgress_DropsBackToRecordCompletedStageCount()
    {
        var row = new TaskRowViewModel(NewTask(6));
        row.MarkRunning();
        row.RecordStageCompleted(10);
        Assert.Equal(StageFraction(10), row.ProgressFraction, precision: 6);

        row.MarkIdle();

        Assert.Equal(StageFraction(6), row.ProgressFraction, precision: 6);
    }
}
