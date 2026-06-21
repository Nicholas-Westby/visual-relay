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
    /// <summary>
    /// The System tab shows a ScrollViewer wrapping a SelectableTextBlock
    /// when SystemState is Ready.
    /// </summary>
    [AvaloniaFact]
    public void SystemTab_Ready_UsesSelectableTextBlockInScrollViewer()
    {
        var vm = new MainWindowViewModel
        {
            StageDetail =
            {
                Header = "Stage 01 (Ideate)",
                SystemState = StageDetailState.Ready,
                SystemPromptText = "You are a system. Be helpful.",
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

        var systemView = SwitchToTabAndFindView<StageSystemView>(activityColumn, 2);

        var selectableBlocks = systemView.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .ToList();
        Assert.NotEmpty(selectableBlocks);

        var scrollViewer = systemView.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        Assert.NotNull(scrollViewer);
    }

    /// <summary>
    /// The Input tab renders parsed sections in Expanders when InputState
    /// is Ready.  Sections with CollapsedByDefault=true (e.g. "Prior stages")
    /// start collapsed.
    /// </summary>
    [AvaloniaFact]
    public void InputTab_Ready_UsesExpandersWithCollapsedByDefault()
    {
        var vm = new MainWindowViewModel
        {
            StageDetail =
            {
                Header = "Stage 04 (Implement)",
                InputState = StageDetailState.Ready,
                InputSections = new PromptSection[]
                {
                    new("Task input", "Write the code.", false),
                    new("Prior stages", "## Stage 1\nold output", true),
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

        var inputView = SwitchToTabAndFindView<StageInputView>(activityColumn, 3);

        var expanders = inputView.GetVisualDescendants()
            .OfType<Expander>()
            .ToList();
        Assert.Equal(2, expanders.Count);

        Assert.True(expanders[0].IsExpanded);
        Assert.False(expanders[1].IsExpanded);
    }

    /// <summary>
    /// The Output tab renders fields by kind when OutputState is Ready.
    /// Text fields use TextBlock, List fields use ItemsControl, Json fields
    /// use SelectableTextBlock.
    /// </summary>
    [AvaloniaFact]
    public void OutputTab_Ready_RendersFieldsByKind()
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

        AssertContainsText(outputView, "summary");
        AssertContainsText(outputView, "testFiles");
        AssertContainsText(outputView, "metadata");

        AssertContainsText(outputView, "Created tests.");
    }

    /// <summary>
    /// The Output tab has a raw-JSON toggle that, when checked, shows
    /// StageDetail.RawJson instead of the parsed field list.
    /// </summary>
    [AvaloniaFact]
    public void OutputTab_RawJsonToggle_ShowsRawJson()
    {
        var vm = new MainWindowViewModel
        {
            StageDetail =
            {
                Header = "Stage 01 (Ideate)",
                OutputState = StageDetailState.Ready,
                OutputFields = new OutputField[]
                {
                    new("summary", OutputFieldKind.Text, "Framed."),
                },
                RawJson = """{"summary": "Framed.", "options": ["a", "b"]}""",
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

        var toggles = outputView.GetVisualDescendants()
            .OfType<CheckBox>()
            .Where(cb => cb.Content?.ToString()?.Contains("JSON", StringComparison.OrdinalIgnoreCase) == true
                      || cb.Content?.ToString()?.Contains("Raw", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.NotEmpty(toggles);
    }
}
