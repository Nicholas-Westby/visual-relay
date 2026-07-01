using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Structural anchors for the status-footer expand affordance.
///
/// The footer (Grid.Row="2", IsVisible when !ShowHfGate) must host
/// a named <c>StatusExpandButton</c> that carries a <see cref="Flyout"/>
/// whose content includes a <see cref="ScrollViewer"/> and a
/// <see cref="SelectableTextBlock"/> bound to <c>StatusText</c>.
///
/// These tests catch regressions where the flyout is accidentally
/// removed or the ScrollViewer nesting is broken.
/// </summary>
[Collection("Headless")]
public sealed class StatusFooterFlyoutTests
{
    /// <summary>
    /// The footer must contain the named expand button after layout,
    /// and that button must carry a Flyout (not just a tooltip).
    /// </summary>
    [AvaloniaFact]
    public void StatusFooter_HasExpandButton_WithFlyout()
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() }),
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var queuePanel = window.GetVisualDescendants()
            .OfType<QueuePanel>()
            .Single();

        var expandButton = queuePanel.FindControl<Button>("StatusExpandButton");
        Assert.NotNull(expandButton);
        Assert.NotNull(expandButton.Flyout);
        Assert.IsType<Flyout>(expandButton.Flyout);
    }

    /// <summary>
    /// The Flyout content must contain a ScrollViewer that wraps a
    /// SelectableTextBlock — the accessible, scrollable full-text view.
    /// The ScrollViewer must exist in the Flyout's visual content tree
    /// regardless of whether the Flyout is currently open.
    /// </summary>
    [AvaloniaFact]
    public void StatusFooter_FlyoutContent_HasScrollViewerWithSelectableTextBlock()
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() }),
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var queuePanel = window.GetVisualDescendants()
            .OfType<QueuePanel>()
            .Single();

        var expandButton = queuePanel.FindControl<Button>("StatusExpandButton");
        Assert.NotNull(expandButton);

        var flyout = Assert.IsType<Flyout>(expandButton.Flyout);

        // The Flyout's Content (set as the Border in XAML) must be a Border
        // containing a ScrollViewer which in turn contains a SelectableTextBlock.
        var flyoutBorder = flyout.Content as Border;
        Assert.NotNull(flyoutBorder);

        // Walk into the Border > Grid > ScrollViewer.
        var grid = flyoutBorder.Child as Grid;
        Assert.NotNull(grid);

        ScrollViewer? scrollViewer = null;
        foreach (var child in grid.Children)
        {
            if (child is ScrollViewer sv)
            {
                scrollViewer = sv;
                break;
            }
        }
        Assert.NotNull(scrollViewer);

        // The ScrollViewer must contain a SelectableTextBlock.
        var selectableTextBlock = scrollViewer.Content as SelectableTextBlock;
        Assert.NotNull(selectableTextBlock);
    }

    /// <summary>
    /// When StatusText is non-empty the expand button must be visible;
    /// when it is empty or null the button must be hidden (converter driven).
    /// The initial value of StatusText is "Idle" (non-empty), so the button
    /// must already be visible right after layout — guards against a vacuous
    /// test that only checks visibility after an explicit assignment.
    /// </summary>
    [AvaloniaFact]
    public void StatusExpandButton_Visibility_TracksStatusText()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var queuePanel = window.GetVisualDescendants()
            .OfType<QueuePanel>()
            .Single();

        var expandButton = queuePanel.FindControl<Button>("StatusExpandButton");
        Assert.NotNull(expandButton);

        // Initial state: StatusText == "Idle" (non-empty), so button is visible.
        Assert.True(expandButton.IsVisible,
            "Expand button must be visible on startup because StatusText starts as 'Idle'.");

        // Set a non-empty status — button must remain visible.
        vm.StatusText = "Planning some-task…";
        Dispatcher.UIThread.RunJobs();
        Assert.True(expandButton.IsVisible,
            "Expand button must be visible when StatusText is non-empty.");

        // Clear status — button must hide.
        vm.StatusText = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.False(expandButton.IsVisible,
            "Expand button must be hidden when StatusText is empty.");
    }

    /// <summary>
    /// Opening the flyout and pumping the dispatcher must result in the
    /// <see cref="SelectableTextBlock"/> inside displaying the current
    /// <c>StatusText</c> value — verifies that the binding actually resolves
    /// and is not just structurally present.
    /// </summary>
    [AvaloniaFact]
    public void StatusFlyout_WhenOpened_SelectableTextBlockReflectsStatusText()
    {
        const string longStatus =
            "UNIQUE-FLYOUT-BINDING-PROBE: line 1 of a very long status message that " +
            "would normally be clipped in the four-line footer view.\n" +
            "Line 2: more detail about the ongoing operation.\n" +
            "Line 3: even more information about progress and current state.\n" +
            "Line 4: final line of detail to ensure the flyout scrolls.";

        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.StatusText = longStatus;
        Dispatcher.UIThread.RunJobs();

        var queuePanel = window.GetVisualDescendants()
            .OfType<QueuePanel>()
            .Single();

        var expandButton = queuePanel.FindControl<Button>("StatusExpandButton");
        Assert.NotNull(expandButton);

        // Open the flyout so its visual tree is materialised.
        expandButton.Flyout!.ShowAt(expandButton);
        Dispatcher.UIThread.RunJobs();

        // Navigate the flyout content tree: Border > Grid > ScrollViewer > SelectableTextBlock.
        var flyout = Assert.IsType<Flyout>(expandButton.Flyout);
        var flyoutBorder = flyout.Content as Border;
        Assert.NotNull(flyoutBorder);

        var grid = flyoutBorder.Child as Grid;
        Assert.NotNull(grid);

        ScrollViewer? scrollViewer = null;
        foreach (var child in grid.Children)
        {
            if (child is ScrollViewer sv) { scrollViewer = sv; break; }
        }
        Assert.NotNull(scrollViewer);

        var selectableTextBlock = scrollViewer.Content as SelectableTextBlock;
        Assert.NotNull(selectableTextBlock);

        // The binding must have resolved to the exact value we set.
        Assert.Equal(longStatus, selectableTextBlock.Text);
    }
}
