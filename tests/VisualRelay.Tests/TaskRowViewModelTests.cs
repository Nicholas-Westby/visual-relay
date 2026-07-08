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
        Assert.Equal(1.0, new TaskRowViewModel(NewTask(12)).ProgressFraction, precision: 6);
        Assert.Equal(5 / 12.0, new TaskRowViewModel(NewTask(5)).ProgressFraction, precision: 6);
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

    // ── 4-state matrix sweep ──────────────────────────────────────────────

    /// <summary>
    /// Pins the full visual-property matrix across all four card states
    /// in a single parametrized sweep so no future change can drift a single
    /// property without the test catching it.
    /// </summary>
    public static TheoryData<string, bool, bool, object> MatrixData =>
        new()
        {
            // (label, isSelected, isRunning, expected)
            // ── Default ──
            { "default-RailBrush",              false, false, "Transparent" },
            { "default-CardBackgroundBrush",    false, false, "#171A20" },
            { "default-SelectedHighlightBorderBrush", false, false, "Transparent" },
            { "default-SelectedHighlightBorderThickness", false, false, new Thickness(0) },
            { "default-SelectedHighlightShadow", false, false, NoShadow },
            { "default-CardBorderBrush",        false, false, "#2A303A" },
            { "default-CardBorderThickness",    false, false, new Thickness(1) },
            { "default-CardShadow",             false, false, NoShadow },

            // ── Selected ──
            { "selected-RailBrush",              true, false, "#3191FF" },
            { "selected-CardBackgroundBrush",    true, false, "#16233D" },
            { "selected-SelectedHighlightBorderBrush", true, false, "#3191FF" },
            { "selected-SelectedHighlightBorderThickness", true, false, new Thickness(2) },
            { "selected-SelectedHighlightShadow", true, false, SelectedShadow },
            { "selected-CardBorderBrush",        true, false, "#2A303A" },
            { "selected-CardBorderThickness",    true, false, new Thickness(1) },
            { "selected-CardShadow",             true, false, NoShadow },

            // ── Running ──
            { "running-RailBrush",              false, true, "#5AD47D" },
            { "running-CardBackgroundBrush",    false, true, "#14231B" },
            { "running-SelectedHighlightBorderBrush", false, true, "Transparent" },
            { "running-SelectedHighlightBorderThickness", false, true, new Thickness(0) },
            { "running-SelectedHighlightShadow", false, true, NoShadow },
            { "running-CardBorderBrush",        false, true, "#5AD47D" },
            { "running-CardBorderThickness",    false, true, new Thickness(2) },
            { "running-CardShadow",             false, true, RunningShadow },

            // ── Running+Selected (combined) ──
            { "combined-RailBrush",              true, true, "#5AD47D" },
            { "combined-CardBackgroundBrush",    true, true, "#14231B" },
            { "combined-SelectedHighlightBorderBrush", true, true, "#3191FF" },
            { "combined-SelectedHighlightBorderThickness", true, true, new Thickness(2) },
            { "combined-SelectedHighlightShadow", true, true, SelectedShadow },
            { "combined-CardBorderBrush",        true, true, "#5AD47D" },
            { "combined-CardBorderThickness",    true, true, new Thickness(2) },
            { "combined-CardShadow",             true, true, NoShadow },
        };

    [Theory]
    [MemberData(nameof(MatrixData))]
    public void VisualState_Matrix_PropertyIsCorrect(
        string label, bool isSelected, bool isRunning, object expected)
    {
        var row = new TaskRowViewModel(NewTask()) { IsSelected = isSelected };
        if (isRunning) row.MarkRunning();

        var prop = label[(label.IndexOf('-') + 1)..];
        var actual = typeof(TaskRowViewModel).GetProperty(prop)!.GetValue(row);

        if (expected is string hex)
        {
            var expectedBrush = hex == "Transparent"
                ? Brushes.Transparent
                : Brush.Parse(hex);
            AssertBrushColor((IBrush)expectedBrush, (IBrush)actual!);
        }
        else if (expected is Thickness t)
        {
            Assert.Equal(t, (Thickness)actual!);
        }
        else if (expected is BoxShadows s)
        {
            Assert.Equal(s, (BoxShadows)actual!);
        }
        else
        {
            Assert.Equal(expected, actual);
        }
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
