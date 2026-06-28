using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed partial class ActivityColumnTabsUiTests
{
    /// <summary>The Output tab renders fields as accordions (Expander per field) with
    /// selectable body text for all field kinds, mirroring the Input tab's pattern.</summary>
    [AvaloniaFact]
    public void OutputTab_Ready_RendersFieldsAsExpandersWithSelectableBodies()
    {
        var vm = new MainWindowViewModel
        {
            StageDetail =
            {
                Header = "Stage 05 (Author-tests)",
                OutputState = StageDetailState.Ready,
                OutputFields = new OutputField[]
                {
                    new("summary", OutputFieldKind.Text, "Created tests."),
                    new("testFiles", OutputFieldKind.List, "a.cs\nb.cs"),
                    new("metadata", OutputFieldKind.Json, """{"count": 3}"""),
                },
            }
        };

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);
        var outputView = SwitchToTabAndFindView<StageOutputView>(activityColumn, 4);

        var expanders = outputView.GetVisualDescendants()
            .OfType<Expander>()
            .ToList();
        Assert.Equal(3, expanders.Count);
        Assert.All(expanders, e => Assert.True(e.IsExpanded));

        var summaryExpander = expanders.FirstOrDefault(e => e.Header?.ToString() == "summary");
        Assert.NotNull(summaryExpander);

        var selectableBlocks = outputView.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Where(stb => stb.IsVisible)
            .ToList();
        Assert.Contains(selectableBlocks, stb => stb.Text == "Created tests.");
        Assert.Contains(selectableBlocks, stb => stb.Text == "a.cs\nb.cs");
        Assert.Contains(selectableBlocks, stb => stb.Text == """{"count": 3}""");
    }
}
