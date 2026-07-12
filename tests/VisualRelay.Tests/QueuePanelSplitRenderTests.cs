using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.DesignTime;
using VisualRelay.App.Views.Controls;
using VisualRelay.App.Views.Controls.Buttons;

namespace VisualRelay.Tests;

/// <summary>
/// Rendering anchor: each extracted control must host stand-alone in a bare
/// Window with its own <see cref="DesignData"/> context, proving the
/// previewer can render them without the full MainWindow boot.
/// </summary>
[Collection("Headless")]
public sealed class QueuePanelSplitRenderTests
{
    /// <summary>
    /// <see cref="TaskCard"/> hosted alone with <see cref="DesignData.Card"/>
    /// must produce a <c>Border.queueCard</c> descendant and a single
    /// <see cref="ProgressBar"/> whose value matches the design data.
    /// </summary>
    [AvaloniaFact]
    public void TaskCard_RendersStandalone_FromDesignData()
    {
        var card = new TaskCard { DataContext = DesignData.Card };
        var window = new Window
        {
            Content = card,
            Width = 340,
            Height = 200
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var queueCard = window.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("queueCard"));
        Assert.NotNull(queueCard);

        var progressBar = window.GetVisualDescendants()
            .OfType<ProgressBar>()
            .Single();
        Assert.Equal(DesignData.Card.ProgressFraction, progressBar.Value);
    }

    /// <summary>
    /// <see cref="QueueFooter"/> hosted alone with <see cref="DesignData.Main"/>
    /// must contain a named <c>StatusExpandButton</c> with a <see cref="Flyout"/>
    /// in its own namescope.
    /// </summary>
    [AvaloniaFact]
    public void QueueFooter_HostsExpandButton_InOwnNameScope()
    {
        var footer = new QueueFooter { DataContext = DesignData.Main };
        var window = new Window
        {
            Content = footer,
            Width = 340,
            Height = 200
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var expandButton = footer.FindControl<CommonButton>("StatusExpandButton");
        Assert.NotNull(expandButton);
        Assert.NotNull(expandButton.Flyout);
    }
}
