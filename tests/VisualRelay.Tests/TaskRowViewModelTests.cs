using Avalonia;
using Avalonia.Media;
using VisualRelay.App.ViewModels;
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

    // ── Default state (neither selected nor running) ──────────────────────

    [Fact]
    public void VisualState_Default_HasNoSelectedHighlight()
    {
        var row = new TaskRowViewModel(NewTask());

        AssertBrushColor(Transparent, row.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(0), row.SelectedHighlightBorderThickness);
        Assert.Equal(NoShadow, row.SelectedHighlightShadow);
    }

    [Fact]
    public void VisualState_Default_HasWaitingInnerBorder()
    {
        var row = new TaskRowViewModel(NewTask());

        AssertBrushColor(WaitingBorder, row.CardBorderBrush);
        Assert.Equal(new Thickness(1), row.CardBorderThickness);
        Assert.Equal(NoShadow, row.CardShadow);
    }

    [Fact]
    public void VisualState_Default_HasWaitingCardBackground()
    {
        var row = new TaskRowViewModel(NewTask());

        AssertBrushColor(Brush.Parse("#171A20"), row.CardBackgroundBrush);
    }

    [Fact]
    public void VisualState_Default_HasTransparentRail()
    {
        var row = new TaskRowViewModel(NewTask());

        AssertBrushColor(Transparent, row.RailBrush);
    }

    // ── Selected-only state ───────────────────────────────────────────────

    [Fact]
    public void VisualState_Selected_HasBlueOuterHighlight()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };

        AssertBrushColor(SelectedBorder, row.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(2), row.SelectedHighlightBorderThickness);
        Assert.Equal(SelectedShadow, row.SelectedHighlightShadow);
    }

    [Fact]
    public void VisualState_Selected_HasWaitingInnerBorder()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };

        AssertBrushColor(WaitingBorder, row.CardBorderBrush);
        Assert.Equal(new Thickness(1), row.CardBorderThickness);
    }

    [Fact]
    public void VisualState_Selected_HasNoInnerShadow()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };

        Assert.Equal(NoShadow, row.CardShadow);
    }

    [Fact]
    public void VisualState_Selected_HasSelectedCardBackground()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };

        AssertBrushColor(Brush.Parse("#16233D"), row.CardBackgroundBrush);
    }

    [Fact]
    public void VisualState_Selected_HasBlueRail()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };

        AssertBrushColor(SelectedBorder, row.RailBrush);
    }

    // ── Running-only state ────────────────────────────────────────────────

    [Fact]
    public void VisualState_Running_HasNoSelectedHighlight()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();

        AssertBrushColor(Transparent, row.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(0), row.SelectedHighlightBorderThickness);
        Assert.Equal(NoShadow, row.SelectedHighlightShadow);
    }

    [Fact]
    public void VisualState_Running_HasGreenInnerBorder()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();

        AssertBrushColor(RunningBorder, row.CardBorderBrush);
        Assert.Equal(new Thickness(2), row.CardBorderThickness);
        Assert.Equal(RunningShadow, row.CardShadow);
    }

    [Fact]
    public void VisualState_Running_HasRunningCardBackground()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();

        AssertBrushColor(Brush.Parse("#14231B"), row.CardBackgroundBrush);
    }

    [Fact]
    public void VisualState_Running_HasGreenRail()
    {
        var row = new TaskRowViewModel(NewTask());
        row.MarkRunning();

        AssertBrushColor(RunningBorder, row.RailBrush);
    }

    // ── Combined active+in-progress state ─────────────────────────────────

    [Fact]
    public void VisualState_Combined_HasBlueOuterAndGreenInnerBorder()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };
        row.MarkRunning();

        // Outer border is blue (selected highlight)
        AssertBrushColor(SelectedBorder, row.SelectedHighlightBorderBrush);
        Assert.Equal(new Thickness(2), row.SelectedHighlightBorderThickness);
        Assert.Equal(SelectedShadow, row.SelectedHighlightShadow);

        // Inner border is green (running), thickness 2
        AssertBrushColor(RunningBorder, row.CardBorderBrush);
        Assert.Equal(new Thickness(2), row.CardBorderThickness);

        // No inner shadow — the outer border carries the shadow
        Assert.Equal(NoShadow, row.CardShadow);
    }

    [Fact]
    public void VisualState_Combined_HasRunningBackgroundAndGreenRail()
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = true };
        row.MarkRunning();

        AssertBrushColor(Brush.Parse("#14231B"), row.CardBackgroundBrush);
        AssertBrushColor(RunningBorder, row.RailBrush);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void AssertBrushColor(IBrush expected, IBrush actual)
    {
        var expectedSolid = Assert.IsAssignableFrom<ISolidColorBrush>(expected);
        var actualSolid = Assert.IsAssignableFrom<ISolidColorBrush>(actual);
        Assert.Equal(expectedSolid.Color, actualSolid.Color);
    }

    private static RelayTaskItem NewTask(int completedStageCount = 0) =>
        new("a", "/tmp/a.md", "/tmp", false, [], CompletedStageCount: completedStageCount);
}
