using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Layout regressions for the ACTIVITY column's Input/Output accordion lists
/// (the shared ScrollViewer -> ItemsControl shell). Both defects are asserted
/// against the *measured* panel content width, because the column is
/// user-resizable: (1) every item stretches to the full panel width, and
/// (2) the last item's bottom is inside the ScrollViewer's scrollable extent.
/// </summary>
public sealed partial class ActivityColumnTabsUiTests
{
    // Tall enough that even one long section overflows the ~576px viewport, so
    // the "scroll reaches the bottom" assertion is meaningful.
    private static readonly string LongBody = string.Join(
        "\n",
        Enumerable.Range(1, 16).Select(i => $"Line {i} of a long body that grows the section vertically for the scroll test."));

    /// <summary>
    /// Every Input <see cref="Expander"/> must arrange to the full content width
    /// of the panel. A vertical StackPanel offers but does not force the cross
    /// width, so short-content items render narrow today; this asserts they all
    /// match the measured panel content width.
    /// </summary>
    [AvaloniaFact]
    public void InputTab_Ready_AllExpandersStretchToPanelWidth()
    {
        var vm = BuildInputViewModel();
        var window = ShowWindow(vm);

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);
        var inputView = SwitchToTabAndFindView<StageInputView>(activityColumn!, 3);
        Dispatcher.UIThread.RunJobs();

        var itemsControl = FindReadyItemsControl(inputView);
        var expanders = inputView.GetVisualDescendants().OfType<Expander>().ToList();
        Assert.True(expanders.Count >= 3, $"expected >=3 expanders, got {expanders.Count}");

        var panelContentWidth = itemsControl.Bounds.Width;
        Assert.True(panelContentWidth > 0, "items control has no measured width");

        // Each item Border has Margin=4 (8px horizontal) and BorderThickness=1
        // (2px), so a fully-stretched Expander fills (content width - 10).
        var expected = panelContentWidth - 10.0;
        foreach (var expander in expanders)
        {
            Assert.True(
                Math.Abs(expander.Bounds.Width - expected) <= 1.0,
                $"expander '{expander.Header}' width {expander.Bounds.Width:0.##} != panel content {expected:0.##}");
        }

        // The core defect: short items must not be narrower than long ones.
        var widths = expanders.Select(e => e.Bounds.Width).ToList();
        Assert.True(
            widths.Max() - widths.Min() <= 1.0,
            $"expander widths are ragged: {string.Join(", ", widths.Select(w => w.ToString("0.##")))}");
    }

    /// <summary>
    /// The user must be able to scroll until the last Input item's bottom edge
    /// is visible: the ScrollViewer's vertical extent has to contain the last
    /// item plus a deliberate bottom gap.
    /// </summary>
    [AvaloniaFact]
    public void InputTab_Ready_LastItemBottomIsInsideScrollExtent()
    {
        var vm = BuildInputViewModel();
        var window = ShowWindow(vm);

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);
        var inputView = SwitchToTabAndFindView<StageInputView>(activityColumn!, 3);
        Dispatcher.UIThread.RunJobs();

        var scrollViewer = FindReadyScrollViewer(inputView);
        var itemsControl = FindReadyItemsControl(inputView);

        // Content must overflow so the assertion is meaningful.
        Assert.True(
            itemsControl.Bounds.Height > scrollViewer.Viewport.Height,
            $"content ({itemsControl.Bounds.Height:0.##}) does not overflow viewport ({scrollViewer.Viewport.Height:0.##})");

        var lastBorder = LastItemBorder(itemsControl);
        var lastBottom = ContentBottom(lastBorder, scrollViewer);

        Assert.True(
            scrollViewer.Extent.Height >= lastBottom,
            $"extent {scrollViewer.Extent.Height:0.##} does not reach last item bottom {lastBottom:0.##}");
        // And a real, small gap beneath it (the bottom inset is part of extent).
        Assert.True(
            scrollViewer.Extent.Height - lastBottom >= 2.0,
            $"no bottom gap: extent {scrollViewer.Extent.Height:0.##} vs bottom {lastBottom:0.##}");
    }

    /// <summary>
    /// Output tab parity for width: each output field <see cref="Border"/> must
    /// stretch to the panel content width (Output items are Borders containing
    /// Expanders, sharing the same ScrollViewer/ItemsControl shell).
    /// </summary>
    [AvaloniaFact]
    public void OutputTab_Ready_AllFieldBordersStretchToPanelWidth()
    {
        var vm = BuildOutputViewModel();
        var window = ShowWindow(vm);

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);
        var outputView = SwitchToTabAndFindView<StageOutputView>(activityColumn!, 4);
        Dispatcher.UIThread.RunJobs();

        var itemsControl = FindReadyItemsControl(outputView);
        var borders = ItemBorders(itemsControl);
        Assert.True(borders.Count >= 3, $"expected >=3 field borders, got {borders.Count}");

        var panelContentWidth = itemsControl.Bounds.Width;
        Assert.True(panelContentWidth > 0, "items control has no measured width");

        var expected = panelContentWidth - 8.0; // item Border Margin=4 (8px horizontal)
        foreach (var border in borders)
        {
            Assert.True(
                Math.Abs(border.Bounds.Width - expected) <= 1.0,
                $"field border width {border.Bounds.Width:0.##} != panel content {expected:0.##}");
        }

        var widths = borders.Select(b => b.Bounds.Width).ToList();
        Assert.True(
            widths.Max() - widths.Min() <= 1.0,
            $"field border widths are ragged: {string.Join(", ", widths.Select(w => w.ToString("0.##")))}");
    }

    /// <summary>
    /// Output tab parity for scroll: the last field's bottom must be inside the
    /// ScrollViewer extent with a deliberate bottom gap.
    /// </summary>
    [AvaloniaFact]
    public void OutputTab_Ready_LastFieldBottomIsInsideScrollExtent()
    {
        var vm = BuildOutputViewModel();
        var window = ShowWindow(vm);

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);
        var outputView = SwitchToTabAndFindView<StageOutputView>(activityColumn!, 4);
        Dispatcher.UIThread.RunJobs();

        var scrollViewer = FindReadyScrollViewer(outputView);
        var itemsControl = FindReadyItemsControl(outputView);

        Assert.True(
            itemsControl.Bounds.Height > scrollViewer.Viewport.Height,
            $"content ({itemsControl.Bounds.Height:0.##}) does not overflow viewport ({scrollViewer.Viewport.Height:0.##})");

        var lastBorder = LastItemBorder(itemsControl);
        var lastBottom = ContentBottom(lastBorder, scrollViewer);

        Assert.True(
            scrollViewer.Extent.Height >= lastBottom,
            $"extent {scrollViewer.Extent.Height:0.##} does not reach last field bottom {lastBottom:0.##}");
        Assert.True(
            scrollViewer.Extent.Height - lastBottom >= 2.0,
            $"no bottom gap: extent {scrollViewer.Extent.Height:0.##} vs bottom {lastBottom:0.##}");
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    private static MainWindowViewModel BuildInputViewModel() => new()
    {
        StageDetail =
        {
            Header = "Stage 04 (Implement)",
            InputState = StageDetailState.Ready,
            InputSections = new PromptSection[]
            {
                new("Header", "x", false),
                new("Task input", LongBody, false),
                new("Output contract", LongBody, false),
            },
        }
    };

    private static MainWindowViewModel BuildOutputViewModel() => new()
    {
        StageDetail =
        {
            Header = "Stage 05 (Author-tests)",
            OutputState = StageDetailState.Ready,
            OutputFields = new OutputField[]
            {
                new("summary", OutputFieldKind.Text, "x"),
                new("testFiles", OutputFieldKind.List, LongBody),
                new("metadata", OutputFieldKind.Json, LongBody),
            },
        }
    };

    // ── helpers ──────────────────────────────────────────────────────────

    private static MainWindow ShowWindow(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>The visible "Ready" ScrollViewer (the parsed-sections branch).</summary>
    private static ScrollViewer FindReadyScrollViewer(Control view)
    {
        var sv = view.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(s => s.IsVisible && s.GetVisualDescendants().OfType<ItemsControl>().Any());
        Assert.NotNull(sv);
        return sv!;
    }

    private static ItemsControl FindReadyItemsControl(Control view)
    {
        var ic = view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.IsVisible);
        Assert.NotNull(ic);
        return ic!;
    }

    /// <summary>The item-container Borders directly produced by the ItemsControl.</summary>
    private static List<Border> ItemBorders(ItemsControl itemsControl)
    {
        // Each item realizes as a ContentPresenter whose first Border child is
        // the item container Border from the DataTemplate.
        var borders = new List<Border>();
        foreach (var presenter in itemsControl.GetVisualDescendants().OfType<ContentPresenter>())
        {
            var border = presenter.GetVisualChildren().OfType<Border>().FirstOrDefault();
            if (border is not null)
                borders.Add(border);
        }

        Assert.NotEmpty(borders);
        return borders;
    }

    private static Border LastItemBorder(ItemsControl itemsControl) => ItemBorders(itemsControl)[^1];

    /// <summary>Bottom of <paramref name="control"/> in the ScrollViewer's content space.</summary>
    private static double ContentBottom(Visual control, ScrollViewer scrollViewer)
    {
        var presenter = scrollViewer.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var topLeft = control.TranslatePoint(new Point(0, 0), presenter) ?? new Point(0, 0);
        return topLeft.Y + scrollViewer.Offset.Y + control.Bounds.Height;
    }
}
