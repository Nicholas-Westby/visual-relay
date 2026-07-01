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
    /// The QUEUE title TextBlock must render at its full desired width when the
    /// panel is constrained to a provably narrow 200 px — narrower than what
    /// the old `*`-column layout needed to avoid clipping.
    ///
    /// We verify by confirming that the TextBlock's rendered Bounds.Width
    /// matches (within 1 px) the DesiredSize.Width reported after layout.
    /// A clipped star-column assignment causes Bounds.Width to be smaller
    /// than DesiredSize.Width.
    ///
    /// This test is self-contained: it constrains the panel itself to 200 px
    /// rather than relying on MainWindow.axaml pinning the queue to 280 px.
    /// </summary>
    [AvaloniaFact]
    public void QueueTitle_RendersAtFullWidth_NotClipped()
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

        // Constrain the panel to 200 px — provably narrow enough that the old
        // *-title-column layout would clip the "QUEUE" text.  This makes the
        // visual assertion independent of whatever width MainWindow.axaml assigns.
        queuePanel.Width = 200;
        queuePanel.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

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

    /// <summary>
    /// The header Grid's column structure must match the fixed layout:
    ///   Col 0 — Auto  (title TextBlock)
    ///   Col 1 — Auto  (chip count badge)
    ///   Col 2 — *     (spacer that absorbs slack so title is never squeezed)
    ///   Cols 3-5      (buttons — not checked here)
    ///
    /// This structural check is independent of any rendered width and will
    /// catch a regression even if the panel is given unlimited space.
    /// </summary>
    [AvaloniaFact]
    public void QueueHeader_ColumnStructure_TitleIsAuto_SpacerIsStar()
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

        // Find the panelTitle TextBlock.
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

        // The title TextBlock must be in column 0 of its parent Grid.
        Assert.IsType<Grid>(titleBlock.Parent);
        var headerGrid = (Grid)titleBlock.Parent!;
        Assert.Equal(0, Grid.GetColumn(titleBlock));

        // Column 0 must be Auto (not *) so the title always gets its natural width.
        Assert.True(headerGrid.ColumnDefinitions.Count >= 3,
            $"Header grid must have at least 3 columns; found {headerGrid.ColumnDefinitions.Count}.");

        var col0 = headerGrid.ColumnDefinitions[0].Width;
        Assert.True(col0.GridUnitType == GridUnitType.Auto,
            $"Column 0 (title) must be Auto — found {col0.GridUnitType}. " +
            "A * column would clip the title at narrow widths.");

        // A star spacer column must exist somewhere after column 0 so there is
        // a pressure-absorber.  Without it, the buttons would crowd the title.
        var hasStarSpacer = headerGrid.ColumnDefinitions
            .Skip(1)
            .Any(c => c.Width.GridUnitType == GridUnitType.Star);

        Assert.True(hasStarSpacer,
            "Header grid must contain a * spacer column after column 0 to absorb slack.");
    }
}
