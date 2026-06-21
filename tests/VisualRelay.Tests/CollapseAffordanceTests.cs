using Avalonia.Controls;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class CollapseAffordanceTests
{
    /// <summary>
    /// When a panel is collapsed, its rail title must be wrapped in a
    /// <see cref="LayoutTransformControl"/> (not a bare
    /// <see cref="TextBlock.RenderTransform"/>), and the transformed label
    /// must fit within the 36 px rail width.
    /// </summary>
    [AvaloniaFact]
    public void CollapsedRail_UsesLayoutTransformControl_AndTitleFitsWithinRail()
    {
        var (viewModel, window) = CreateWindow();
        viewModel.IsQueueCollapsed = true;
        Dispatcher.UIThread.RunJobs();

        FindVisualOfType(window.Content as Control, out Border? railBorder,
            b => b.IsVisible && b.Classes.Contains("rail"));
        Assert.NotNull(railBorder);

        var stackPanel = railBorder.Child as StackPanel;
        Assert.NotNull(stackPanel);
        Assert.True(stackPanel.IsVisible);

        LayoutTransformControl? layoutTransform = null;
        foreach (var child in stackPanel.Children)
        {
            if (child is LayoutTransformControl ltc) { layoutTransform = ltc; break; }
        }
        Assert.NotNull(layoutTransform);
        Assert.True(layoutTransform.IsVisible);

        var titleBlock = layoutTransform.Child as TextBlock;
        Assert.NotNull(titleBlock);
        Assert.Equal("QUEUE", titleBlock.Text);

        var bounds = layoutTransform.Bounds;
        Assert.True(bounds.Width > 0, "LayoutTransformControl width should be > 0");
        Assert.True(bounds.Height > 0, "LayoutTransformControl height should be > 0");
        Assert.True(bounds.Width <= 36,
            $"LayoutTransformControl width {bounds.Width} should fit within rail width 36");

        foreach (var child in stackPanel.Children)
        {
            if (child is TextBlock tb) Assert.Null(tb.RenderTransform);
        }
    }

    /// <summary>
    /// The collapse/expand toggle buttons must all use the unified
    /// <c>collapseToggle</c> style class — the old <c>railToggle</c>
    /// class must not appear anywhere in the visual tree.
    /// </summary>
    [AvaloniaFact]
    public void AllToggleButtons_UseUnifiedCollapseToggleClass()
    {
        var (viewModel, window) = CreateWindow();

        var allButtons = new List<Button>();
        CollectControls(window.Content as Control, allButtons);
        foreach (var btn in allButtons)
        {
            Assert.False(btn.Classes.Contains("railToggle"),
                $"Button with content '{btn.Content}' should not use railToggle class");
        }

        viewModel.IsQueueCollapsed = true;
        Dispatcher.UIThread.RunJobs();

        allButtons.Clear();
        CollectControls(window.Content as Control, allButtons);
        var collapseButtons = allButtons.FindAll(b => b.Classes.Contains("collapseToggle"));
        Assert.NotEmpty(collapseButtons);
    }

    /// <summary>
    /// ToolTip strings on toggle buttons must match the resolved action
    /// and align with the glyph direction.
    /// </summary>
    [AvaloniaFact]
    public void CollapseToggleTooltips_MatchGlyphDirection()
    {
        var (viewModel, window) = CreateWindow();
        viewModel.IsQueueCollapsed = true;
        Dispatcher.UIThread.RunJobs();

        var allButtons = new List<Button>();
        CollectControls(window.Content as Control, allButtons);

        var visibleCollapseButtons = allButtons.FindAll(
            b => b.Classes.Contains("collapseToggle") && b.IsVisible);
        foreach (var btn in visibleCollapseButtons)
        {
            var tip = ToolTip.GetTip(btn) as string;
            Assert.NotNull(tip);
            Assert.NotEmpty(tip);
        }

        // Rail toggles must say "Expand X".
        var railButtons = allButtons.FindAll(
            b => b.Classes.Contains("collapseToggle")
                && b is { IsVisible: true, Parent: StackPanel { Parent: Border brd } }
                && brd.Classes.Contains("rail"));
        Assert.NotEmpty(railButtons);
        foreach (var btn in railButtons)
        {
            var tip = ToolTip.GetTip(btn) as string;
            Assert.NotNull(tip);
            Assert.StartsWith("Expand ", tip);
        }
    }

    /// <summary>
    /// Header toggle tooltips must flip between "Collapse X" (expanded) and
    /// "Expand X" (collapsed) to match the chevron glyph direction.
    /// FAILS until dynamic tooltip properties are added to the ViewModel.
    /// </summary>
    [AvaloniaFact]
    public void HeaderToggleTooltips_FlipWithCollapseState()
    {
        var (viewModel, window) = CreateWindow();

        // ── Queue ──
        FindVisualOfType(window.Content as Control, out QueuePanel? queuePanel);
        Assert.NotNull(queuePanel);

        FindVisualOfType(queuePanel, out Button? queueToggle,
            b => b.Classes.Contains("collapseToggle"));
        Assert.NotNull(queueToggle);

        Assert.Equal("Collapse Queue", ToolTip.GetTip(queueToggle) as string);

        viewModel.IsQueueCollapsed = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Expand Queue", ToolTip.GetTip(queueToggle) as string);

        viewModel.IsQueueCollapsed = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Collapse Queue", ToolTip.GetTip(queueToggle) as string);

        // ── Stages ──
        FindVisualOfType(window.Content as Control, out StageBoard? stageBoard);
        Assert.NotNull(stageBoard);

        FindVisualOfType(stageBoard, out Button? stagesToggle,
            b => b.Classes.Contains("collapseToggle"));
        Assert.NotNull(stagesToggle);

        Assert.Equal("Collapse Stages", ToolTip.GetTip(stagesToggle) as string);

        viewModel.IsStagesCollapsed = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Expand Stages", ToolTip.GetTip(stagesToggle) as string);

        viewModel.IsStagesCollapsed = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Collapse Stages", ToolTip.GetTip(stagesToggle) as string);

        // ── Activity ──
        FindVisualOfType(window.Content as Control, out ActivityColumn? activityColumn);
        Assert.NotNull(activityColumn);

        var activityToggles = new List<Button>();
        CollectControls(activityColumn, activityToggles);
        var headerToggles = activityToggles.FindAll(
            b => b.Classes.Contains("collapseToggle"));
        Assert.Single(headerToggles);

        var activityToggle = headerToggles[0];
        Assert.Equal("Collapse Activity", ToolTip.GetTip(activityToggle) as string);

        viewModel.IsActivityColumnCollapsed = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Expand Activity", ToolTip.GetTip(activityToggle) as string);

        viewModel.IsActivityColumnCollapsed = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Collapse Activity", ToolTip.GetTip(activityToggle) as string);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static (MainWindowViewModel vm, MainWindow window) CreateWindow()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window);
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
