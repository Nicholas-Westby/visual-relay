using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for markdown/attachments scroll-bottom clipping in
/// <see cref="TaskDetailPanel"/>.  The fix moves the bottom inset from
/// <see cref="ScrollViewer.Padding"/> onto the inner content element as
/// <see cref="Border.Margin"/> so the bottom gap is part of the measured
/// extent and the last item stays reachable (same pattern as
/// <see cref="StageInputView"/> and <see cref="StageOutputView"/>).
/// </summary>
[Collection("Headless")]
public sealed class TaskDetailScrollBottomReachabilityTests
{
    private const string OverflowingMarkdown =
        "# Scroll Reach Test\n\n" +
        "Line 01: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 02: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 03: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 04: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 05: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 06: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 07: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 08: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 09: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 10: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 11: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 12: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 13: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 14: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 15: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 16: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 17: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 18: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 19: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 20: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 21: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 22: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 23: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 24: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 25: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 26: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 27: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 28: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 29: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 30: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 31: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 32: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 33: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 34: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 35: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 36: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 37: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 38: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 39: The quick brown fox jumps over the lazy dog repeatedly.\n" +
        "Line 40: The quick brown fox jumps over the lazy dog repeatedly.";

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Create a MainWindow loaded with <paramref name="repo"/>, select the
    /// task with id <paramref name="taskId"/>, switch to
    /// <paramref name="tabIndex"/>, and return the <see cref="TaskDetailPanel"/>.
    /// </summary>
    private static async Task<TaskDetailPanel> LoadPanelAsync(
        TestRepository repo, string taskId, int tabIndex, int height = 600)
    {
        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == taskId);
        await vm.LastSelectionLoad!;
        vm.SelectedTabIndex = tabIndex;
        Dispatcher.UIThread.RunJobs();
        var w = new MainWindow { DataContext = vm, Width = 900, Height = height };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w.GetVisualDescendants().OfType<TaskDetailPanel>().Single();
    }

    /// <summary>Find a visible ScrollViewer whose Content is a TextBlock with the given LineHeight.</summary>
    private static (ScrollViewer sv, TextBlock tb) FindTextBlockScroller(
        TaskDetailPanel panel, double lineHeight)
    {
        foreach (var sv in panel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            if (!sv.IsVisible) continue;
            if (sv.Content is TextBlock tb && Math.Abs(tb.LineHeight - lineHeight) < 0.5)
                return (sv, tb);
        }
        throw new InvalidOperationException("No visible ScrollViewer with matching TextBlock found.");
    }

    /// <summary>Find a visible ScrollViewer whose Content is an ItemsControl.</summary>
    private static (ScrollViewer sv, ItemsControl ic) FindItemsControlScroller(
        TaskDetailPanel panel)
    {
        var sv = panel.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.IsVisible && s.Content is ItemsControl);
        var ic = (ItemsControl)sv.Content!;
        return (sv, ic);
    }

    /// <summary>Bottom edge of <paramref name="control"/> in the ScrollViewer's content space.</summary>
    private static double ContentBottom(Visual control, ScrollViewer sv)
    {
        var p = sv.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var tl = control.TranslatePoint(new Point(0, 0), p) ?? new Point(0, 0);
        return tl.Y + sv.Offset.Y + control.Bounds.Height;
    }

    /// <summary>The last item container Border from an ItemsControl.</summary>
    private static Border LastItemBorder(ItemsControl ic)
    {
        var borders = new List<Border>();
        foreach (var cp in ic.GetVisualDescendants().OfType<ContentPresenter>())
        {
            var b = cp.GetVisualChildren().OfType<Border>().FirstOrDefault();
            if (b is not null) borders.Add(b);
        }
        Assert.NotEmpty(borders);
        return borders[^1];
    }

    // ── Structural: Markdown read-only ──────────────────────────────────

    /// <summary>Markdown read-only TextBlock.Margin.Bottom must be ≥ 16 px.</summary>
    [AvaloniaFact]
    public async Task MarkdownReadOnly_TextBlock_MarginBottom_IsSixteenOrMore()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("scroll-markdown", OverflowingMarkdown);
        var panel = await LoadPanelAsync(repo, "scroll-markdown", 0);
        var (_, tb) = FindTextBlockScroller(panel, 21);
        Assert.True(tb.Margin.Bottom >= 16,
            $"Margin.Bottom={tb.Margin.Bottom}, need ≥16. ScrollViewer.Padding bottom is outside measured extent.");
    }

    // ── Structural: Context ─────────────────────────────────────────────

    /// <summary>Context tab TextBlock.Margin.Bottom must be ≥ 16 px.</summary>
    [AvaloniaFact]
    public async Task ContextTab_TextBlock_MarginBottom_IsSixteenOrMore()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("scroll-context", "# Context Scroll\n\n" +
            string.Join("\n", Enumerable.Range(1, 30).Select(i =>
                $"Context line {i:D2}: some context information here.")));
        var panel = await LoadPanelAsync(repo, "scroll-context", 1);
        var (_, tb) = FindTextBlockScroller(panel, 21);
        Assert.True(tb.Margin.Bottom >= 16,
            $"Margin.Bottom={tb.Margin.Bottom}, need ≥16. ScrollViewer.Padding bottom is outside measured extent.");
    }

    // ── Structural: Attachments ─────────────────────────────────────────

    /// <summary>Attachments ItemsControl.Margin.Bottom must be ≥ 16 px.</summary>
    [AvaloniaFact]
    public async Task AttachmentsTab_ItemsControl_MarginBottom_IsSixteenOrMore()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var siblings = Enumerable.Range(1, 20)
            .Select(i => ($"attachment-file-{i:D2}.txt", $"content {i}"))
            .ToArray();
        repo.WriteNestedTask("scroll-attachments", "# Attachments\n", siblings);
        var panel = await LoadPanelAsync(repo, "scroll-attachments", 2);
        var (_, ic) = FindItemsControlScroller(panel);
        Assert.True(ic.Margin.Bottom >= 16,
            $"Margin.Bottom={ic.Margin.Bottom}, need ≥16. ScrollViewer.Padding bottom is outside measured extent.");
    }

    // ── Behavioural: Markdown extent reaches last line ──────────────────

    /// <summary>Markdown ScrollViewer.Extent.Height must reach TextBlock bottom with ≥2 px gap.</summary>
    [AvaloniaFact]
    public async Task MarkdownReadOnly_Extent_ReachesTextBlockBottom_WithGap()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("scroll-md-extent", OverflowingMarkdown);
        var panel = await LoadPanelAsync(repo, "scroll-md-extent", 0, height: 900);
        Dispatcher.UIThread.RunJobs();
        var (sv, tb) = FindTextBlockScroller(panel, 21);
        Assert.True(tb.Bounds.Height > sv.Viewport.Height, "Content must overflow viewport.");
        var bottom = ContentBottom(tb, sv);
        Assert.True(sv.Extent.Height >= bottom,
            $"Extent {sv.Extent.Height:0.##} does not reach TextBlock bottom {bottom:0.##}");
        Assert.True(sv.Extent.Height - bottom >= 2.0,
            $"No bottom gap: extent {sv.Extent.Height:0.##} vs {bottom:0.##}. 16 px margin must be in measured extent.");
    }

    // ── Behavioural: Attachments extent reaches last item ───────────────

    /// <summary>Attachments ScrollViewer.Extent.Height must reach last item bottom with ≥2 px gap.</summary>
    [AvaloniaFact]
    public async Task AttachmentsTab_Extent_ReachesLastItemBottom_WithGap()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var siblings = Enumerable.Range(1, 20)
            .Select(i => ($"attachment-file-{i:D2}.txt", $"content {i}"))
            .ToArray();
        repo.WriteNestedTask("scroll-att-extent", "# Attachments Extent\n", siblings);
        var panel = await LoadPanelAsync(repo, "scroll-att-extent", 2);
        var (sv, ic) = FindItemsControlScroller(panel);
        Assert.True(ic.Bounds.Height > sv.Viewport.Height, "Content must overflow viewport.");
        var last = LastItemBorder(ic);
        var bottom = ContentBottom(last, sv);
        Assert.True(sv.Extent.Height >= bottom,
            $"Extent {sv.Extent.Height:0.##} does not reach last item bottom {bottom:0.##}");
        Assert.True(sv.Extent.Height - bottom >= 2.0,
            $"No bottom gap: extent {sv.Extent.Height:0.##} vs {bottom:0.##}. 16 px margin must be in measured extent.");
    }
}
