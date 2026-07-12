using Avalonia;
using Avalonia.Media;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TaskRowViewModelTests
{
    private static readonly IBrush Transparent = Brushes.Transparent;
    private static readonly IBrush WaitingBorder = Brush.Parse("#2A303A");
    private static readonly IBrush SelectedBorder = Brush.Parse("#3191FF");
    private static readonly IBrush RunningBorder = Brush.Parse("#5AD47D");
    private static readonly BoxShadows NoShadow = BoxShadows.Parse("0 0 0 0 #00000000");
    private static readonly BoxShadows SelectedShadow = BoxShadows.Parse("0 0 22 0 #553F8CFF");
    private static readonly BoxShadows RunningShadow = BoxShadows.Parse("0 0 22 0 #445AD47D");

    [Fact]
    public void ProgressFraction_IsZeroWithNoRunHistory()
    {
        Assert.Equal(0d, new TaskRowViewModel(NewTask()).ProgressFraction);
    }

    [Fact]
    public void ProgressFraction_ScalesWithCompletedStageCount()
    {
        var d = (double)RelayStages.All.Count;
        Assert.Equal(1.0, new TaskRowViewModel(NewTask(RelayStages.All.Count)).ProgressFraction, precision: 6);
        Assert.Equal(5 / d, new TaskRowViewModel(NewTask(5)).ProgressFraction, precision: 6);
        Assert.Equal(1.0, new TaskRowViewModel(NewTask(99)).ProgressFraction, precision: 6);
    }

    [Fact]
    public void VisualState_Default_HasNoSelectedHighlight()
    {
        var r = new TaskRowViewModel(NewTask());
        AssertBrushColor(Transparent, r.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(0), r.SelectedHighlightBorderThickness);
        Assert.Equal(NoShadow, r.SelectedHighlightShadow);
    }

    [Fact]
    public void VisualState_Default_HasWaitingInnerBorder()
    {
        var r = new TaskRowViewModel(NewTask());
        AssertBrushColor(WaitingBorder, r.CardBorderBrush);
        Assert.Equal(new Thickness(1), r.CardBorderThickness);
        Assert.Equal(NoShadow, r.CardShadow);
    }

    [Fact]
    public void VisualState_Default_HasWaitingCardBackground()
    {
        AssertBrushColor(Brush.Parse("#171A20"), new TaskRowViewModel(NewTask()).CardBackgroundBrush);
    }

    [Fact]
    public void VisualState_Default_HasTransparentRail()
    {
        AssertBrushColor(Transparent, new TaskRowViewModel(NewTask()).RailBrush);
    }

    [Fact]
    public void VisualState_Selected_HasBlueOuterHighlight()
    {
        var r = new TaskRowViewModel(NewTask()) { IsSelected = true };
        AssertBrushColor(SelectedBorder, r.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(2), r.SelectedHighlightBorderThickness);
        Assert.Equal(SelectedShadow, r.SelectedHighlightShadow);
    }

    [Fact]
    public void VisualState_Selected_HasWaitingInnerBorder()
    {
        var r = new TaskRowViewModel(NewTask()) { IsSelected = true };
        AssertBrushColor(WaitingBorder, r.CardBorderBrush);
        Assert.Equal(new Thickness(1), r.CardBorderThickness);
    }

    [Fact]
    public void VisualState_Selected_HasNoInnerShadow()
    {
        Assert.Equal(NoShadow, new TaskRowViewModel(NewTask()) { IsSelected = true }.CardShadow);
    }

    [Fact]
    public void VisualState_Selected_HasSelectedCardBackground()
    {
        AssertBrushColor(Brush.Parse("#16233D"), new TaskRowViewModel(NewTask()) { IsSelected = true }.CardBackgroundBrush);
    }

    [Fact]
    public void VisualState_Selected_HasBlueRail()
    {
        AssertBrushColor(SelectedBorder, new TaskRowViewModel(NewTask()) { IsSelected = true }.RailBrush);
    }

    [Fact]
    public void VisualState_Running_HasNoSelectedHighlight()
    {
        var r = new TaskRowViewModel(NewTask()); r.MarkRunning();
        AssertBrushColor(Transparent, r.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(0), r.SelectedHighlightBorderThickness);
        Assert.Equal(NoShadow, r.SelectedHighlightShadow);
    }

    [Fact]
    public void VisualState_Running_HasGreenInnerBorder()
    {
        var r = new TaskRowViewModel(NewTask()); r.MarkRunning();
        AssertBrushColor(RunningBorder, r.CardBorderBrush);
        Assert.Equal(new Thickness(2), r.CardBorderThickness);
        Assert.Equal(RunningShadow, r.CardShadow);
    }

    [Fact]
    public void VisualState_Running_HasRunningCardBackground()
    {
        var r = new TaskRowViewModel(NewTask()); r.MarkRunning();
        AssertBrushColor(Brush.Parse("#14231B"), r.CardBackgroundBrush);
    }

    [Fact]
    public void VisualState_Running_HasGreenRail()
    {
        var r = new TaskRowViewModel(NewTask()); r.MarkRunning();
        AssertBrushColor(RunningBorder, r.RailBrush);
    }

    [Fact]
    public void VisualState_Combined_HasBlueOuterAndGreenInnerBorder()
    {
        var r = new TaskRowViewModel(NewTask()) { IsSelected = true }; r.MarkRunning();
        AssertBrushColor(SelectedBorder, r.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(2), r.SelectedHighlightBorderThickness);
        Assert.Equal(SelectedShadow, r.SelectedHighlightShadow);
        AssertBrushColor(RunningBorder, r.CardBorderBrush);
        Assert.Equal(new Thickness(2), r.CardBorderThickness);
        Assert.Equal(NoShadow, r.CardShadow);
    }

    [Fact]
    public void VisualState_Combined_HasRunningBackgroundAndGreenRail()
    {
        var r = new TaskRowViewModel(NewTask()) { IsSelected = true }; r.MarkRunning();
        AssertBrushColor(Brush.Parse("#14231B"), r.CardBackgroundBrush);
        AssertBrushColor(RunningBorder, r.RailBrush);
    }

    public static TheoryData<string, bool, bool, object> MatrixData => new()
    {
        { "default-RailBrush", false, false, "Transparent" },
        { "default-CardBackgroundBrush", false, false, "#171A20" },
        { "default-SelectedHighlightBorderBrush", false, false, "Transparent" },
        { "default-SelectedHighlightBorderThickness", false, false, new Thickness(0) },
        { "default-SelectedHighlightShadow", false, false, NoShadow },
        { "default-CardBorderBrush", false, false, "#2A303A" },
        { "default-CardBorderThickness", false, false, new Thickness(1) },
        { "default-CardShadow", false, false, NoShadow },
        { "selected-RailBrush", true, false, "#3191FF" },
        { "selected-CardBackgroundBrush", true, false, "#16233D" },
        { "selected-SelectedHighlightBorderBrush", true, false, "#3191FF" },
        { "selected-SelectedHighlightBorderThickness", true, false, new Thickness(2) },
        { "selected-SelectedHighlightShadow", true, false, SelectedShadow },
        { "selected-CardBorderBrush", true, false, "#2A303A" },
        { "selected-CardBorderThickness", true, false, new Thickness(1) },
        { "selected-CardShadow", true, false, NoShadow },
        { "running-RailBrush", false, true, "#5AD47D" },
        { "running-CardBackgroundBrush", false, true, "#14231B" },
        { "running-SelectedHighlightBorderBrush", false, true, "Transparent" },
        { "running-SelectedHighlightBorderThickness", false, true, new Thickness(0) },
        { "running-SelectedHighlightShadow", false, true, NoShadow },
        { "running-CardBorderBrush", false, true, "#5AD47D" },
        { "running-CardBorderThickness", false, true, new Thickness(2) },
        { "running-CardShadow", false, true, RunningShadow },
        { "combined-RailBrush", true, true, "#5AD47D" },
        { "combined-CardBackgroundBrush", true, true, "#14231B" },
        { "combined-SelectedHighlightBorderBrush", true, true, "#3191FF" },
        { "combined-SelectedHighlightBorderThickness", true, true, new Thickness(2) },
        { "combined-SelectedHighlightShadow", true, true, SelectedShadow },
        { "combined-CardBorderBrush", true, true, "#5AD47D" },
        { "combined-CardBorderThickness", true, true, new Thickness(2) },
        { "combined-CardShadow", true, true, NoShadow },
    };

    [Theory]
    [MemberData(nameof(MatrixData))]
    public void VisualState_Matrix_PropertyIsCorrect(string label, bool isSelected, bool isRunning, object expected)
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = isSelected };
        if (isRunning) row.MarkRunning();
        var prop = label[(label.IndexOf('-') + 1)..];
        var actual = typeof(TaskRowViewModel).GetProperty(prop)!.GetValue(row);
        if (expected is string hex)
            AssertBrushColor(hex == "Transparent" ? Brushes.Transparent : Brush.Parse(hex), (IBrush)actual!);
        else if (expected is Thickness t)
            Assert.Equal(t, (Thickness)actual!);
        else if (expected is BoxShadows s)
            Assert.Equal(s, (BoxShadows)actual!);
        else
            Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProgressFraction_UsesRelayStagesDenominator()
    {
        var d = (double)RelayStages.All.Count;
        var half = RelayStages.All.Count / 2;
        Assert.Equal(1.0, new TaskRowViewModel(NewTask(RelayStages.All.Count)).ProgressFraction, precision: 6);
        Assert.Equal(half / d, new TaskRowViewModel(NewTask(half)).ProgressFraction, precision: 6);
    }

    [Fact]
    public void ProgressFraction_UsesLiveCountWhenRunning()
    {
        var row = new TaskRowViewModel(NewTask());
        Assert.Equal(0.0, row.ProgressFraction);
        row.MarkRunning();
        Assert.Equal(0.0, row.ProgressFraction);
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        for (var i = 1; i <= RelayStages.All.Count; i++)
            row.RecordStageCompleted(i);
        Assert.Equal(1.0, row.ProgressFraction, precision: 6);
        Assert.Equal(RelayStages.All.Count, changed.Count(p => p == "ProgressFraction"));
    }

    [Fact]
    public void ProgressFraction_FallsBackToRunMetricsWhenIdle()
    {
        var d = (double)RelayStages.All.Count;
        var row = new TaskRowViewModel(NewTask(6));
        Assert.Equal(6.0 / d, row.ProgressFraction, precision: 6);
        row.MarkRunning();
        row.RecordStageCompleted(1); row.RecordStageCompleted(2); row.RecordStageCompleted(3);
        Assert.Equal(3.0 / d, row.ProgressFraction, precision: 6);
        row.MarkIdle();
        Assert.Equal(6.0 / d, row.ProgressFraction, precision: 6);
    }

    [Fact]
    public void UpdateTask_NotifiesProgressFraction()
    {
        var changed = new List<string>();
        var row = new TaskRowViewModel(NewTask(2));
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        row.UpdateTask(NewTask(8));
        Assert.Contains("ProgressFraction", changed);
    }

    [Fact]
    public void RecordStageCompleted_RaisesPropertyChangedForProgressFraction()
    {
        var row = new TaskRowViewModel(NewTask()); row.MarkRunning();
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        row.RecordStageCompleted(1);
        Assert.Contains("ProgressFraction", changed);
    }

    [Fact]
    public void RunningStepLabel_SingleStage()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning(7, "Review", [7]);
        Assert.Equal("Stage 07 · Review", row.RunningStepLabel);
    }

    [Fact]
    public void RunningStepLabel_ConcurrentPair()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning(8, "Visual-review", [7, 8]);
        var label = row.RunningStepLabel;
        Assert.DoesNotContain(" & ", label, StringComparison.Ordinal);
        Assert.Contains("+", label, StringComparison.Ordinal);
        Assert.Contains("Review", label, StringComparison.Ordinal);
        Assert.Contains("Visual-review", label, StringComparison.Ordinal);
        Assert.Contains(" ∥ ", label, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningStepLabel_EmptySetWhileRunning()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning(null, null, new HashSet<int>());
        Assert.Equal("Running task", row.RunningStepLabel);
    }

    private static void AssertBrushColor(IBrush expected, IBrush actual)
    {
        var e = Assert.IsAssignableFrom<ISolidColorBrush>(expected);
        var a = Assert.IsAssignableFrom<ISolidColorBrush>(actual);
        Assert.Equal(e.Color, a.Color);
    }

    private static RelayTaskItem NewTask(int completedStageCount = 0) =>
        new("a", "/tmp/a.md", "/tmp", false, [], CompletedStageCount: completedStageCount);
}
