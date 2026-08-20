using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.ViewModels.RunLogRows;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Pins the run-log attention colouring contract: a <c>warn</c>/<c>error</c>
/// event's header line must resolve to the attention red <c>#F36F63</c>, while a
/// routine <c>info</c> event's header stays the default blue <c>#53B7F4</c>.
/// These are headless render tests in the style of
/// <see cref="RunLogGroupingRenderTests"/>: they host a real
/// <see cref="RunLogView"/> and inspect the resolved
/// <see cref="TextBlock.Foreground"/> of the header, not just the
/// <see cref="RelayEvent.IsAttention"/> flag.
/// </summary>
[Collection("Headless")]
public sealed class RunLogHeaderAttentionTests
{
    private static readonly Color AttentionRed = Color.Parse("#F36F63");
    private static readonly Color HeaderBlue = Color.Parse("#53B7F4");

    /// <summary>
    /// A <c>warn</c> event is an attention event, so its header line must take
    /// the attention red instead of the routine blue.
    /// </summary>
    [AvaloniaFact]
    public void WarnEvent_HeaderResolvesToAttentionRed()
    {
        var warn = new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "tests_red", "run-1", "/root",
            "task-1", 2, "cheap",
            Data: new Dictionary<string, string> { ["reason"] = "2 failing before implementation" });

        var header = HostAndFindHeader(new SingleEventRow(warn));

        Assert.Equal(AttentionRed, ((ISolidColorBrush)header.Foreground!).Color);
    }

    /// <summary>
    /// An <c>info</c> event is routine, so its header line must keep the default
    /// blue and never take the attention red.
    /// </summary>
    [AvaloniaFact]
    public void InfoEvent_HeaderKeepsDefaultBlue()
    {
        var info = new RelayEvent(
            DateTimeOffset.UtcNow, "info", "stage_start", "run-1", "/root",
            "task-1", 3, "balanced");

        var header = HostAndFindHeader(new SingleEventRow(info));

        Assert.Equal(HeaderBlue, ((ISolidColorBrush)header.Foreground!).Color);
    }

    /// <summary>
    /// Hosts a <see cref="RunLogView"/> whose <see cref="MainWindowViewModel.Events"/>
    /// contains exactly the given row, then returns the header
    /// <see cref="TextBlock"/> for that row. The exact-type filter
    /// (<c>tb.GetType() == typeof(TextBlock)</c>) excludes the
    /// <see cref="SelectableTextBlock"/> detail line, which derives from
    /// <see cref="TextBlock"/> and carries the same data context.
    /// </summary>
    private static TextBlock HostAndFindHeader(SingleEventRow row)
    {
        var vm = new MainWindowViewModel(
            new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        vm.Events.Add(row);

        var runLogView = new RunLogView { DataContext = vm };
        var host = new Window { Content = runLogView, Width = 600, Height = 400 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        return runLogView.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.GetType() == typeof(TextBlock))
            .Single(tb => tb.Text == row.DisplayLine);
    }
}
