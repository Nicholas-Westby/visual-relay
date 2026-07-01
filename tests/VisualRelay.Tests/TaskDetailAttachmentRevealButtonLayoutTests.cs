using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the attachment Reveal button spacing defect.
///
/// Root cause: when the attachment item template was changed from a Grid
/// (which had ColumnSpacing=&quot;6&quot;) to a DockPanel, the Reveal
/// button kept a Margin of &quot;0,0,6,0&quot; — a gap only on its right.
/// The file-path TextBlock (the DockPanel fill child) sits directly against
/// the left edge of the Reveal button with no gap, causing the trimmed
/// filename to butt against the button.
///
/// Fix: give the Reveal button Margin=&quot;6,0,6,0&quot; to restore the
/// ~6 px gap between the path text and the button.
/// </summary>
[Collection("Headless")]
public sealed class TaskDetailAttachmentRevealButtonLayoutTests
{
    /// <summary>
    /// The Reveal button in the attachment item template must have a left
    /// margin of at least 6 px so the file-path TextBlock (the DockPanel
    /// fill child that sits to its left) does not butt directly against it.
    ///
    /// This is a structural property check — the Margin.Left guards the gap
    /// independently of layout measurements.
    /// </summary>
    [AvaloniaFact]
    public async Task AttachmentRevealButton_MarginLeft_IsSixOrMore()
    {
        // ── Arrange: task with an attachment ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        repo.WriteNestedTask("gap-check", "# Gap Check\n", ("file.txt", "content"));

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "gap-check");
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

        // ── Locate the Reveal button inside the Attachments ScrollViewer ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Button? revealButton = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var btn = sv.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString() == "Reveal");
            if (btn is not null)
            {
                revealButton = btn;
                break;
            }
        }

        Assert.NotNull(revealButton);

        // ── Assertion: left margin ≥ 6 ──
        Assert.True(
            revealButton!.Margin.Left >= 6,
            $"Reveal button Margin.Left is {revealButton.Margin.Left}, " +
            "must be ≥ 6.  A zero left margin causes the file-path TextBlock " +
            "to butt directly against the Reveal button with no gap.");
    }

    /// <summary>
    /// Visual guard: the rendered horizontal gap between the file-path
    /// TextBlock's right edge and the Reveal button's left edge must be at
    /// least 4 px (allowing for rounding).  This catches any regression
    /// that would shrink the gap even if the Margin property were somehow
    /// present.
    /// </summary>
    [AvaloniaFact]
    public async Task AttachmentFilePath_HasGapBeforeRevealButton()
    {
        // ── Arrange: task with an attachment whose path is long enough to
        //    exercise the ellipsis-and-gap region ──
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var longName = "very-long-attachment-filename-that-would-overlap-with-reveal-button.txt";
        repo.WriteNestedTask("gap-visual", "# Gap Visual\n", (longName, "content"));

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "gap-visual");
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

        // ── Locate the Reveal button, then find its sibling path TextBlock ──
        var taskDetailPanel = window.GetVisualDescendants()
            .OfType<TaskDetailPanel>()
            .Single();

        Button? revealButton = null;
        foreach (var sv in taskDetailPanel.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var btn = sv.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString() == "Reveal");
            if (btn is not null)
            {
                revealButton = btn;
                break;
            }
        }
        Assert.NotNull(revealButton);

        // The Reveal button is docked right in a DockPanel; the path TextBlock
        // is the fill child.  Find the TextBlock whose text contains the
        // attachment filename (the path binding is a full filesystem path).
        var dockPanel = revealButton!.Parent as DockPanel;
        Assert.NotNull(dockPanel);

        TextBlock? pathBlock = null;
        foreach (var child in dockPanel!.Children)
        {
            if (child is TextBlock { Text: not null } tb && tb.Text.Contains(longName))
            {
                pathBlock = tb;
                break;
            }
        }

        Assert.NotNull(pathBlock);

        // Translate both edges to a common ancestor (the DockPanel).
        var pathRightEdge = pathBlock.TranslatePoint(
            new Point(pathBlock.Bounds.Width, 0), dockPanel);
        var buttonLeftEdge = revealButton.TranslatePoint(
            new Point(0, 0), dockPanel);

        Assert.NotNull(pathRightEdge);
        Assert.NotNull(buttonLeftEdge);

        var gap = buttonLeftEdge!.Value.X - pathRightEdge!.Value.X;

        Assert.True(gap >= 4.0,
            $"Gap between file-path right edge and Reveal button left edge is {gap:F1} px, " +
            "must be ≥ 4 px. The path text is butting against the Reveal button.");
    }
}
