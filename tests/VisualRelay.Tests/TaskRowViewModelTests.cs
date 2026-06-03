using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TaskRowViewModelTests
{
    [Fact]
    public void ProgressFraction_IsZeroWithNoRunHistory()
    {
        var row = new TaskRowViewModel(NewTask());

        Assert.Equal(0d, row.ProgressFraction);
    }

    [Fact]
    public void ProgressFraction_ScalesWithCompletedStageCount()
    {
        Assert.Equal(1.0, new TaskRowViewModel(NewTask(11)).ProgressFraction, precision: 6);
        Assert.Equal(5 / 11.0, new TaskRowViewModel(NewTask(5)).ProgressFraction, precision: 6);
        Assert.Equal(1.0, new TaskRowViewModel(NewTask(99)).ProgressFraction, precision: 6);
    }

    private static RelayTaskItem NewTask(int completedStageCount = 0) =>
        new("a", "/tmp/a.md", "/tmp", false, [], CompletedStageCount: completedStageCount);
}
