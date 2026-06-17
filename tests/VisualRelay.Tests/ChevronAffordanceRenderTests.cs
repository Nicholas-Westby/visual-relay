using Avalonia.Controls;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Rendering-level guards for the uniform vector chevrons and the distinct,
/// prominent master Focus/Restore toggle. Complements the plain-VM direction
/// assertions in <see cref="MainWindowViewModelLayoutTests"/>.
/// </summary>
[Collection("Headless")]
public sealed class ChevronAffordanceRenderTests
{
    /// <summary>
    /// Every collapse/expand affordance must render the drawn vector
    /// <see cref="ChevronIcon"/> — no panel may fall back to a bound text glyph.
    /// </summary>
    [AvaloniaFact]
    public void ChevronAffordances_RenderVectorIcon_NotTextGlyph()
    {
        var window = CreateWindow();

        var icons = new List<ChevronIcon>();
        CollectControls(window.Content as Control, icons);
        Assert.NotEmpty(icons);

        // There must be at least one chevron per visible toggle (Queue, Stages,
        // Run Log, LLM Commands header chevrons are all present by default).
        Assert.True(icons.Count >= 4,
            $"expected >= 4 chevron icons, found {icons.Count}");

        // The one shared geometry exists and every chevron renders at the one
        // shared fixed size regardless of which way it points.
        Assert.NotNull(ChevronIcon.SharedGeometry);
        foreach (var icon in icons)
        {
            Assert.Equal(ChevronIcon.IconSize, icon.Width);
            Assert.Equal(ChevronIcon.IconSize, icon.Height);
        }
    }

    /// <summary>
    /// The rendered chevron size must be identical between a horizontally-
    /// collapsing panel (Queue) and a vertically-folding one (Stages) — no
    /// repeat of the old large-vs-small mixed-glyph defect.
    /// </summary>
    [AvaloniaFact]
    public void ChevronSize_IsIdentical_AcrossHorizontalAndVerticalPanels()
    {
        var window = CreateWindow();

        FindVisualOfType(window.Content as Control, out QueuePanel? queuePanel);
        Assert.NotNull(queuePanel);
        FindVisualOfType(window.Content as Control, out StageBoard? stageBoard);
        Assert.NotNull(stageBoard);

        FindVisualOfType(queuePanel, out ChevronIcon? queueChevron);
        Assert.NotNull(queueChevron);
        FindVisualOfType(stageBoard, out ChevronIcon? stagesChevron);
        Assert.NotNull(stagesChevron);

        // The Queue toggle collapses horizontally; the Stages toggle folds
        // vertically. They must render at exactly the same bounds.
        Assert.Equal(queueChevron.Bounds.Size, stagesChevron.Bounds.Size);
        Assert.True(queueChevron.Bounds.Width > 0);
        Assert.True(queueChevron.Bounds.Height > 0);
    }

    /// <summary>
    /// The master Focus/Restore toggle must NOT use the per-panel
    /// <c>collapseToggle</c> style and must render larger than the chevron
    /// toggles — it is the primary affordance and reads as such.
    /// </summary>
    [AvaloniaFact]
    public void FocusToggle_IsDistinct_AndLargerThanChevronToggles()
    {
        var window = CreateWindow();

        FindVisualOfType(window.Content as Control, out TaskDetailPanel? detailPanel);
        Assert.NotNull(detailPanel);

        // The focus toggle carries its own focusToggle class, not collapseToggle.
        FindVisualOfType(detailPanel, out Button? focusButton,
            b => b.Classes.Contains("focusToggle"));
        Assert.NotNull(focusButton);
        Assert.False(focusButton.Classes.Contains("collapseToggle"),
            "focus toggle must not reuse the collapseToggle style");

        // It renders its crisp vector expand/contract icon, not a text glyph.
        FindVisualOfType(focusButton, out FocusToggleIcon? focusIcon);
        Assert.NotNull(focusIcon);

        // The focus button is larger than the per-panel chevron toggles.
        var collapseToggles = new List<Button>();
        CollectControls(window.Content as Control, collapseToggles);
        collapseToggles = collapseToggles.FindAll(
            b => b.Classes.Contains("collapseToggle") && b.IsVisible);
        Assert.NotEmpty(collapseToggles);

        var chevronArea = collapseToggles[0].Bounds.Width * collapseToggles[0].Bounds.Height;
        var focusArea = focusButton.Bounds.Width * focusButton.Bounds.Height;
        Assert.True(focusArea > chevronArea,
            $"focus toggle area {focusArea} should exceed chevron toggle area {chevronArea}");
        Assert.True(focusIcon.Width > ChevronIcon.IconSize,
            "focus icon should be drawn larger than a chevron");
    }

    /// <summary>
    /// The shared chevron geometry's ink bounding box must be optically centered
    /// about the midpoint of the icon box (IconSize/2, IconSize/2). Both axes
    /// are checked to within a half-pixel tolerance so a large uncentered path
    /// (like the old x-midpoint at 6.75 vs box centre 6.0) fails the test.
    /// </summary>
    [Fact]
    public void SharedGeometry_IsOpticallycenteredInIconBox()
    {
        var bounds = ChevronIcon.SharedGeometry.Bounds;
        double boxCenter = ChevronIcon.IconSize / 2.0;
        const double tolerance = 0.5; // half-pixel; old path was off by 0.75

        double inkMidX = (bounds.Left + bounds.Right) / 2.0;
        double inkMidY = (bounds.Top + bounds.Bottom) / 2.0;

        Assert.True(Math.Abs(inkMidX - boxCenter) <= tolerance,
            $"Chevron ink x-midpoint {inkMidX:F3} deviates from box centre {boxCenter} by more than {tolerance}px");
        Assert.True(Math.Abs(inkMidY - boxCenter) <= tolerance,
            $"Chevron ink y-midpoint {inkMidY:F3} deviates from box centre {boxCenter} by more than {tolerance}px");
    }

    /// <summary>
    /// The default foreground must be explicitly set so the chevron renders
    /// deterministically even if no style matches.
    /// </summary>
    [Fact]
    public void ChevronForeground_HasExplicitDefault_NotNull()
    {
        var icon = new ChevronIcon();
        Assert.NotNull(icon.Foreground);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static MainWindow CreateWindow()
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static bool FindVisualOfType<T>(Control? root, out T? result,
        Func<T, bool>? predicate = null) where T : Control
    {
        result = null;
        if (root is null) return false;

        if (root is T t && (predicate is null || predicate(t)))
        {
            result = t;
            return true;
        }

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                if (FindVisualOfType(child, out result, predicate)) return true;
        }
        else if (root is Decorator { Child: not null } decorator)
        {
            if (FindVisualOfType(decorator.Child, out result, predicate)) return true;
        }
        else if (root is ContentControl { Content: Control contentChild })
        {
            if (FindVisualOfType(contentChild, out result, predicate)) return true;
        }
        else if (root is LayoutTransformControl { Child: not null } ltc)
        {
            if (FindVisualOfType(ltc.Child, out result, predicate)) return true;
        }

        return false;
    }

    private static void CollectControls<T>(Control? root, List<T> results) where T : Control
    {
        if (root is null) return;
        if (root is T t) results.Add(t);

        if (root is Panel panel)
        {
            foreach (var child in panel.Children) CollectControls(child, results);
        }
        else if (root is Decorator { Child: not null } decorator)
        {
            CollectControls(decorator.Child, results);
        }
        else if (root is ContentControl { Content: Control contentChild })
        {
            CollectControls(contentChild, results);
        }
        else if (root is LayoutTransformControl { Child: not null } ltc)
        {
            CollectControls(ltc.Child, results);
        }
    }
}
