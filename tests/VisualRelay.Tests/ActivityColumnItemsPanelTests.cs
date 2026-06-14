using Avalonia.Controls;
using Avalonia.Threading;
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
        // ── Arrange: show the window with a default ViewModel ──
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // ── Act: locate the trace ListBox ──
        // Traverse: window → ActivityColumn (UserControl) → ListBox x:Name="TraceList"
        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);
        var traceList = activityColumn.FindControl<ListBox>("TraceList");
        Assert.NotNull(traceList);
        Assert.NotNull(traceList.ItemsPanelRoot);

        // ── Assert: the realized items panel is a plain (non-virtualizing) StackPanel ──
        Assert.IsType<StackPanel>(traceList.ItemsPanelRoot);
    }
}
