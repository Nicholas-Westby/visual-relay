using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the Attachments-tab Remove button clipping defect.
///
/// Root cause: the Remove button sits in the rightmost Auto column of a
/// *,Auto,Auto Grid inside a <see cref="Border"/> with Padding="4,3"
/// (only 4 px right padding).  Above that, the Attachments-tab
/// <see cref="ScrollViewer"/> has HorizontalScrollBarVisibility="Disabled"
/// and its VerticalScrollBarVisibility defaults to Auto.  When enough
/// attachments overflow vertically, the vertical scrollbar steals ~12 px of
/// viewport width.  At the window's MinWidth=900 with the ActivityColumn at
/// 300 px, the CenterGrid star column gets only ~280 px — the Remove button
/// has only 4 px of right padding to the ScrollViewer clip boundary, which
/// collapses to zero when the scrollbar appears, clipping the button's
/// right edge.
///
/// This is the same class of star-column-vs-Auto-column clipping defect
/// previously fixed for the QueuePanel title
/// (<see cref="QueuePanelTitleLayoutTests"/> lines 13-19).
///
/// Fix: increase the item template Border Padding from "4,3" to "4,3,10,3"
/// (right padding goes from 4 to 10) so the Remove button has breathing
/// room even when the vertical scrollbar steals viewport width.
/// </summary>
[Collection("Headless")]
public sealed class TaskDetailRemoveButtonLayoutTests
{
    /// <summary>
    /// The Remove button's right edge must lie within the Attachments
    /// ScrollViewer viewport — not clipped by the hard
    /// HorizontalScrollBarVisibility="Disabled" boundary.
    ///
    /// We verify at the window's MinWidth=900 with the ActivityColumn at
    /// its minimum 300 px and 12 attachments (guaranteeing a vertical
    /// scrollbar), which is the worst-case width for the CenterGrid.
    /// </summary>
    [AvaloniaFact]
    public async Task RemoveButton_RightEdge_WithinScrollViewerViewport_NotClipped()
    {
        // ── Arrange: task with 12 attachment siblings ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var siblings = Enumerable.Range(1, 12)
            .Select(i => ($"attachment-file-{i:D2}.txt", $"content {i}"))
            .ToArray();
        repo.WriteNestedTask("clip-repro", "# Clip Repro\n\nTest task for clipping bug.", siblings);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "clip-repro");
        viewModel.SelectedTabIndex = 2; // Attachments tab
        Dispatcher.UIThread.RunJobs();

        // ── Show the window at MinWidth ──
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Locate the Attachments ScrollViewer and the Remove button ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        ScrollViewer? attachmentsScrollViewer = null;
        Button? removeButton = null;

        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var removeButtons = sv.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Content?.ToString() == "Remove")
                .ToList();
            if (removeButtons.Count > 0)
            {
                attachmentsScrollViewer = sv;
                removeButton = removeButtons[0];
                break;
            }
        }

        Assert.NotNull(attachmentsScrollViewer);
        Assert.NotNull(removeButton);
        Assert.True(attachmentsScrollViewer!.Viewport.Width > 0,
            "ScrollViewer viewport must have non-zero width.");

        // ── Assertion A: Remove button right edge within viewport ──
        var presenter = attachmentsScrollViewer
            .GetVisualDescendants()
            .OfType<ScrollContentPresenter>()
            .First();

        // Translate the button's right edge to the presenter's coordinate space.
        var rightEdgeInPresenter = removeButton!.TranslatePoint(
            new Point(removeButton.Bounds.Width, 0), presenter);

        Assert.NotNull(rightEdgeInPresenter);

        var viewportWidth = attachmentsScrollViewer.Viewport.Width;
        var buttonRight = rightEdgeInPresenter.Value.X + attachmentsScrollViewer.Offset.X;

        Assert.True(
            buttonRight <= viewportWidth + 1.0,
            $"Remove button right edge ({buttonRight:F1} px) exceeds " +
            $"ScrollViewer viewport width ({viewportWidth:F1} px). " +
            "The button is clipped — increase right padding on the " +
            "item template Border (line 265) from 4 to at least 10.");
    }

    /// <summary>
    /// Structural guard: the item template <see cref="Border"/>'s
    /// <see cref="Border.Padding"/> Right must be ≥ 10 so the Remove
    /// button has enough breathing room before the ScrollViewer clip
    /// boundary.  This catches any regression that shrinks the padding
    /// back to 4 or lower, even when the window is wide enough to mask
    /// the clipping visually.
    /// </summary>
    [AvaloniaFact]
    public async Task ItemTemplateBorder_PaddingRight_IsTenOrMore()
    {
        // ── Arrange: task with a few attachments ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var siblings = new[]
        {
            ("file-a.txt", "a"),
            ("file-b.txt", "b"),
            ("file-c.txt", "c"),
        };
        repo.WriteNestedTask("pad-check", "# Pad Check\n", siblings);

        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "pad-check");
        viewModel.SelectedTabIndex = 2; // Attachments tab
        Dispatcher.UIThread.RunJobs();

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 900,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Locate the Remove button, then walk up to the item Border ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Button? removeButton = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var btn = sv.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString() == "Remove");
            if (btn is not null)
            {
                removeButton = btn;
                break;
            }
        }

        Assert.NotNull(removeButton);

        // Walk up: Button → DockPanel → Border (the item template Border).
        var dockPanel = removeButton!.Parent as DockPanel;
        Assert.NotNull(dockPanel);

        var dock = DockPanel.GetDock(removeButton);
        Assert.Equal(Dock.Right, dock);

        var border = dockPanel!.Parent as Border;
        Assert.NotNull(border);

        // ── Assertion B: right padding ≥ 10 ──
        Assert.True(
            border!.Padding.Right >= 10,
            $"Item template Border Padding.Right is {border.Padding.Right}, " +
            "must be ≥ 10.  Insufficient right padding is the root cause of " +
            "the Remove button clipping defect.");
    }
}
