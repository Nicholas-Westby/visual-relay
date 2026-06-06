using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

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
    [Fact]
    public async Task TraceListBox_UsesNonVirtualizingStackPanel()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
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

            return 0; // Force Func<Task<int>> overload so exceptions propagate
        }, CancellationToken.None);
    }
}
