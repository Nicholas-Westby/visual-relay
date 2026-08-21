using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class TaskRowViewModelTests
{
    [Fact]
    public void ProgressFraction_SettledStatusRecordDrivesIdleEvenWhenReportCountIsLow()
    {
        // The whole bug: a finished run settles all 12 stages, but its report
        // file count (no stage-12/skipped reports) is only 9. The idle bar must
        // read full from the status record.
        var item = NewTask(9) with { SettledStageCount = 12, PipelineStageCount = 12 };

        Assert.Equal(1.0, new TaskRowViewModel(item).ProgressFraction, precision: 6);
    }

    [Fact]
    public void ProgressFraction_LegacyElevenStageRunReadsFull()
    {
        var item = NewTask(11) with { SettledStageCount = 11, PipelineStageCount = 11 };

        Assert.Equal(1.0, new TaskRowViewModel(item).ProgressFraction, precision: 6);
    }

    [Fact]
    public void ProgressFraction_ZeroPipelineCount_FallsBackToCompletedStageCount()
    {
        var d = (double)RelayStages.All.Count;
        var item = NewTask(5) with { SettledStageCount = 12, PipelineStageCount = 0 };

        Assert.Equal(5 / d, new TaskRowViewModel(item).ProgressFraction, precision: 6);
    }

    [Fact]
    public void ProgressFraction_NoRunHistory_StillZero()
    {
        Assert.Equal(0.0, new TaskRowViewModel(NewTask()).ProgressFraction, precision: 6);
    }

    [Fact]
    public void ProgressFraction_LiveBranchIgnoresSettledCountsAndFallsBackOnIdle()
    {
        var item = NewTask(9) with { SettledStageCount = 12, PipelineStageCount = 12 };
        var row = new TaskRowViewModel(item);
        row.MarkRunning();
        row.SeedCompletedStageCount(6);

        // Running reads the live high-water mark regardless of settled counts.
        Assert.Equal(6 / (double)RelayStages.All.Count, row.ProgressFraction, precision: 6);

        row.MarkIdle();

        // Idle falls back to the settled status record value.
        Assert.Equal(1.0, row.ProgressFraction, precision: 6);
    }
}
