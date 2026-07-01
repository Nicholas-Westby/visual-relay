using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class ActivityColumnItemsPanelTests
{
    /// <summary>
    /// Regression guard: the LLM COMMANDS trace list must use a non-virtualizing
    /// <see cref="StackPanel"/> for its items panel.  If this ListBox ever
    /// reverts to the default <see cref="VirtualizingStackPanel"/> (or any other
    /// panel type), the test fails.
    ///
    /// The RUN LOG list (bound to Events) is intentionally left virtualized and
    /// is not asserted.
    /// </summary>
    [AvaloniaFact]
    public void TraceListBox_UsesNonVirtualizingStackPanel()
    {
        var viewModel = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        // Switch to Commands tab (index 1) to ensure TraceList content is loaded.
        var tabControl = activityColumn.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault();
        Assert.NotNull(tabControl);
        tabControl!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        var commandsView = activityColumn.GetVisualDescendants()
            .OfType<CommandsView>()
            .FirstOrDefault();
        Assert.NotNull(commandsView);

        var traceList = commandsView!.FindControl<ListBox>("TraceList");
        Assert.NotNull(traceList);
        Assert.NotNull(traceList.ItemsPanelRoot);

        Assert.IsType<StackPanel>(traceList.ItemsPanelRoot);
    }
}
