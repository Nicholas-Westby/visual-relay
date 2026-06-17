using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the QUEUE header title clipping defect.
///
/// Root cause: the title TextBlock was placed in a `*` (star) column that
/// competed with the Auto columns for the chip, New, Archive, and chevron
/// buttons. At the panel's normal width the star column shrank below the
/// natural text width, clipping the rightmost glyph of "QUEUE".
///
/// Fix: title column is now `Auto` so it always takes its natural width;
/// a `*` spacer column between the chip and the buttons absorbs the slack.
/// </summary>
[Collection("Headless")]
public sealed class QueuePanelTitleLayoutTests
{
    /// <summary>
    /// The QUEUE title TextBlock must render at its full desired width —
    /// it may not be clipped by its containing column.
    ///
    /// We verify by confirming that the TextBlock's rendered Bounds.Width
    /// matches (within 1 px) the DesiredSize.Width reported after layout.
    /// A clipped star-column assignment causes Bounds.Width to be smaller
    /// than DesiredSize.Width.
    /// </summary>
    [AvaloniaFact]
    public void QueueTitle_RendersAtFullWidth_NotClipped()
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

        var queuePanel = window.GetVisualDescendants()
            .OfType<QueuePanel>()
            .Single();

        // Find the panelTitle TextBlock inside the QueuePanel header grid.
        TextBlock? titleBlock = null;
        foreach (var descendant in queuePanel.GetVisualDescendants().OfType<TextBlock>())
        {
            if (descendant.Classes.Contains("panelTitle"))
            {
                titleBlock = descendant;
                break;
            }
        }

        Assert.NotNull(titleBlock);
        Assert.Equal("QUEUE", titleBlock.Text);

        // The rendered width must be >= the desired width (within 1 px rounding).
        // A clipped `*` column causes Bounds.Width << DesiredSize.Width.
        var desired = titleBlock.DesiredSize.Width;
        var rendered = titleBlock.Bounds.Width;

        Assert.True(rendered > 0,
            "Title TextBlock must have non-zero rendered width.");
        Assert.True(desired > 0,
            "Title TextBlock must have non-zero desired width.");
        Assert.True(rendered >= desired - 1.0,
            $"QUEUE title is clipped: rendered {rendered:F1}px < desired {desired:F1}px. " +
            "The title column must be Auto, not *.");
    }
}
