using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the ACTIVITY panel title vertical misalignment.
///
/// Root cause: the ACTIVITY &quot;panelTitle&quot; TextBlock sits in the same
/// Auto-height header row as the taller &quot;Reveal&quot; button (and the
/// collapse toggle), but unlike the Queue panel's title — which sets
/// VerticalAlignment=&quot;Center&quot; — the ACTIVITY title had no
/// VerticalAlignment, so it rendered top-aligned relative to the button.
///
/// Fix: add VerticalAlignment=&quot;Center&quot; to the ACTIVITY panelTitle
/// TextBlock so it matches the Queue panel and sits level with the Reveal
/// button.
/// </summary>
[Collection("Headless")]
public sealed class ActivityColumnTitleLayoutTests
{
    /// <summary>
    /// The ACTIVITY panelTitle TextBlock must have
    /// VerticalAlignment=&quot;Center&quot; so it aligns vertically with the
    /// taller Reveal button in the same Auto-height header row (matching the
    /// Queue panel pattern at QueuePanel.axaml:18).
    ///
    /// This is a structural property check — no layout measurement needed.
    /// </summary>
    [AvaloniaFact]
    public void ActivityTitle_HasVerticalAlignmentCenter()
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

        var activityColumn = window.GetVisualDescendants()
            .OfType<ActivityColumn>()
            .Single();

        // Find the panelTitle TextBlock with text "ACTIVITY".
        TextBlock? titleBlock = null;
        foreach (var descendant in activityColumn.GetVisualDescendants().OfType<TextBlock>())
        {
            if (descendant.Classes.Contains("panelTitle") && descendant.Text == "ACTIVITY")
            {
                titleBlock = descendant;
                break;
            }
        }

        Assert.NotNull(titleBlock);
        Assert.Equal(VerticalAlignment.Center, titleBlock!.VerticalAlignment);
    }
}
