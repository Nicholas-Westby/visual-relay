using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class ActivityColumnTabsUiTests
{
    /// <summary>
    /// The Run Log tab (index 0) still shows events from VM.Events after
    /// the conversion.  This test validates that the existing Run Log
    /// behaviour is preserved inside the tab structure.
    /// </summary>
    [AvaloniaFact]
    public void RunLog_StillShowsEvents()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        vm.Events.Add(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "stage_start", "run-1", "/tmp",
            "task-1", 1, "cheap", 1));

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

        // Switch to Run Log tab (index 0) to ensure content is loaded.
        var runLogView = SwitchToTabAndFindView<RunLogView>(activityColumn, 0);

        // The Events ListBox should exist somewhere under the RunLogView.
        var listBox = runLogView.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(lb => ReferenceEquals(lb.ItemsSource, vm.Events));
        Assert.NotNull(listBox);
        Assert.True(listBox.ItemCount >= 1);
    }

    /// <summary>
    /// The Commands tab (index 1) still shows TraceEntries from
    /// VM.TraceEntries after the conversion.  This test validates that
    /// the existing LLM Commands behaviour is preserved.
    /// </summary>
    [AvaloniaFact]
    public void CommandsTab_StillShowsTraceEntries()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        vm.TraceEntries.Add(new TraceEntry(
            TraceEntryKind.ToolCall, "verify", "dotnet test", 1));

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

        // Switch to Commands tab (index 1) to ensure TraceList is loaded.
        var commandsView = SwitchToTabAndFindView<CommandsView>(activityColumn, 1);

        var traceList = commandsView.FindControl<ListBox>("TraceList");
        Assert.NotNull(traceList);
        Assert.True(traceList.ItemCount >= 1);
    }
}
