using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Geometry/affordance guards for the ACTIVITY column resize splitter. The
/// on-hover cursor "feel" is a macOS-native behaviour that headless tests cannot
/// confirm; these assert the checkable surface — a comfortable hit width, that
/// the hit strip is co-located with the visible seam (the ACTIVITY panel's left
/// edge, not offset by ColumnSpacing), the resize <see cref="Cursor"/> property,
/// a Fluent-style dot-grip divider with transparent hit-strip Background, seam
/// line matching theme borders, small grip dots (not a heavy block), and that
/// the existing drag→clamp→persist plumbing still updates
/// <see cref="MainWindowViewModel.ActivityColumnWidth"/>.
/// </summary>
[Collection("Headless")]
public sealed class ActivitySplitterAffordanceTests
{
    /// <summary>
    /// The hit target must be a comfortable grab area (≥ 8 px), not the old
    /// 3 px strip that made the splitter "only sometimes" grabbable.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_HitWidth_IsComfortable_NotThreePx()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);

        Assert.True(splitter.Bounds.Width >= 8,
            $"splitter hit width {splitter.Bounds.Width} should be >= 8px (was the old 3px target)");
    }

    /// <summary>
    /// The hit strip must sit on the visible seam — its left edge aligned with
    /// the ACTIVITY panel's left edge — not ~10 px (ColumnSpacing) to the left of
    /// it. The user drags the visible line, so the hit area must be there.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_IsColocated_WithActivitySeam_NotOffsetByColumnSpacing()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);
        var activity = window.GetVisualDescendants().OfType<ActivityColumn>().First();

        double splitterLeft = LeftInWindow(splitter, window);
        double splitterRight = splitterLeft + splitter.Bounds.Width;
        double seam = LeftInWindow(activity, window);

        // The seam must fall within the hit strip's horizontal span (with a small
        // tolerance). The OLD layout placed the strip's right edge ~10px LEFT of
        // the seam, so the seam was entirely outside the strip — this fails it.
        const double tol = 2.0;
        Assert.True(seam >= splitterLeft - tol && seam <= splitterRight + tol,
            $"seam X={seam:F1} must lie within splitter span [{splitterLeft:F1},{splitterRight:F1}] (co-located, not ColumnSpacing-offset)");
    }

    /// <summary>
    /// The splitter must expose an explicit horizontal-resize cursor. The native
    /// on-hover feel can't be headless-verified, but the property must be set.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_ExposesHorizontalResizeCursor()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);

        Assert.NotNull(splitter.Cursor);
        Assert.Equal(new Cursor(StandardCursorType.SizeWestEast).ToString(), splitter.Cursor.ToString());
        Assert.NotEqual(Cursor.Default.ToString(), splitter.Cursor.ToString());
    }

    /// <summary>
    /// The splitter must read as draggable — the hit-strip Background must be
    /// transparent (no solid fill), while template children (SeamLine + grip
    /// dots) provide the visible (non-transparent) divider affordance.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_HasVisibleDivider_NotTransparent()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);

        // The hit-strip Background must be transparent — no solid fill.
        Assert.True(IsTransparent(splitter.Background),
            "splitter Background must be transparent (visible divider comes from template children)");

        // SeamLine must provide the visible (non-transparent) divider line.
        var seamLine = splitter.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "SeamLine");
        Assert.NotNull(seamLine);
        Assert.False(IsTransparent(seamLine.Background),
            "SeamLine must provide a visible (non-transparent) divider line");
    }

    /// <summary>
    /// The seam line must use the theme panel-border colour (#252A33),
    /// harmonising with <c>Border.panel</c> borders throughout the app.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_SeamLine_Color_MatchesThemeBorder()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);
        var seamLine = splitter.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "SeamLine");
        Assert.NotNull(seamLine);

        var gradient = Assert.IsType<LinearGradientBrush>(seamLine.Background);
        Assert.Contains(gradient.GradientStops, gs => gs.Color == Color.Parse("#252A33"));
    }

    /// <summary>
    /// The grip must be a dot pattern (≥ 3 small stacked dots at #46535F),
    /// not the old single 2×34 px solid block. Each dot is ≤ 6×6 px so it
    /// reads as a subtle grab handle rather than a heavy block.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_Grip_IsDotPattern_NotSolidBlock()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);

        // The old "Grip" block (2×34 px) must not exist.
        var oldGrip = splitter.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "Grip");
        Assert.Null(oldGrip);

        // Small dot-like Borders within the splitter (3–6 px square).
        var dots = splitter.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Bounds.Width is >= 3 and <= 6
                     && b.Bounds.Height is >= 3 and <= 6)
            .ToList();

        Assert.True(dots.Count >= 3,
            $"expected ≥ 3 grip dots, found {dots.Count}");

        foreach (var dot in dots)
        {
            Assert.False(IsTransparent(dot.Background),
                "grip dot Background must be non-transparent");
            Assert.Equal(Color.Parse("#46535F"), ((ISolidColorBrush)dot.Background!).Color);
        }
    }

    /// <summary>
    /// Guards the existing resize plumbing: completing a drag must clamp and
    /// persist into <see cref="MainWindowViewModel.ActivityColumnWidth"/>.
    /// Below-minimum widths clamp up to the 300px floor.
    /// </summary>
    [AvaloniaFact]
    public void DragCompleted_ClampsAndPersists_ActivityColumnWidth()
    {
        var (vm, window) = CreateWindow();
        var splitter = FindSplitter(window);
        var contentGrid = window.FindControl<Grid>("ContentGrid")!;

        // A healthy width round-trips unchanged.
        contentGrid.ColumnDefinitions[2].Width = new GridLength(520);
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(520, vm.ActivityColumnWidth);

        // A below-floor width clamps up to the 300px minimum.
        contentGrid.ColumnDefinitions[2].Width = new GridLength(120);
        splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, vm.ActivityColumnWidth);
    }

    /// <summary>
    /// End-to-end: a real pointer drag of the splitter (through its live template
    /// and <c>ResizeBehavior</c>) must resize the ACTIVITY column the user expects
    /// — dragging the seam LEFT widens ACTIVITY. The synthetic
    /// <see cref="DragCompleted_ClampsAndPersists_ActivityColumnWidth"/> only covers
    /// the handler; this proves the GridSplitter→column wiring in the real window.
    /// </summary>
    [AvaloniaFact]
    public void DraggingSeamLeft_WidensActivityColumn()
    {
        var (_, window) = CreateWindow();
        var splitter = FindSplitter(window);
        var contentGrid = window.FindControl<Grid>("ContentGrid")!;

        double before = contentGrid.ColumnDefinitions[2].Width.Value;
        var origin = splitter.TranslatePoint(new Point(0, 0), window) ?? default;
        double x = origin.X + splitter.Bounds.Width / 2;
        const double y = 400.0;

        window.MouseDown(new Point(x, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Point(x - 90, y));
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(new Point(x - 90, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        double after = contentGrid.ColumnDefinitions[2].Width.Value;
        Assert.True(after > before + 40,
            $"dragging the seam left by 90px should widen ACTIVITY (was {before}, now {after})");
    }

    /// <summary>
    /// The collapse gate must remain wired: collapsing the ACTIVITY column hides
    /// the splitter so it can't be grabbed while the column is a thin rail.
    /// </summary>
    [AvaloniaFact]
    public void Splitter_IsHidden_WhenActivityColumnCollapsed()
    {
        var (vm, window) = CreateWindow();
        var splitter = FindSplitter(window);
        Assert.True(splitter.IsVisible);

        vm.IsActivityColumnCollapsed = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(splitter.IsVisible);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static (MainWindowViewModel vm, MainWindow window) CreateWindow()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window);
    }

    private static GridSplitter FindSplitter(MainWindow window) =>
        window.GetVisualDescendants().OfType<GridSplitter>()
            .First(s => s.Name == "ActivitySplitter");

    private static double LeftInWindow(Visual v, Visual root) =>
        (v.TranslatePoint(new Point(0, 0), root) ?? new Point(double.NaN, 0)).X;

    private static bool IsTransparent(IBrush? brush)
    {
        if (brush is null) return true;
        if (brush is ISolidColorBrush solid) return solid.Color.A == 0;
        return false;
    }
}
