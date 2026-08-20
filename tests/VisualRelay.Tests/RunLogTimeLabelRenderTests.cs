using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.ViewModels.RunLogRows;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Pins the run-log timestamp contract at the render layer: every row must show
/// the event's wall-clock time formatted as a fixed local <c>HH:mm:ss</c> label,
/// matching the two console sinks. These are headless render tests in the style
/// of <see cref="RunLogGroupingRenderTests"/>: they host a real
/// <see cref="RunLogView"/> and assert the resolved visual tree contains the
/// expected time text, not just the <see cref="RelayEvent.Timestamp"/> value.
/// </summary>
[Collection("Headless")]
public sealed class RunLogTimeLabelRenderTests
{
    /// <summary>
    /// A single event row renders its timestamp as a local, invariant
    /// <c>HH:mm:ss</c> label. The seeded timestamp is anchored in local time so
    /// the expected label is the exact string regardless of the test machine's
    /// timezone (<c>ToLocalTime()</c> is identity for a <c>DateTimeKind.Local</c>
    /// offset).
    /// </summary>
    [AvaloniaFact]
    public void SingleEventRow_RendersLocalTimestampAsInvariantTime()
    {
        var timestamp = new DateTimeOffset(
            new DateTime(2026, 8, 20, 12, 34, 56, DateTimeKind.Local));
        var evt = new RelayEvent(
            timestamp, "info", "stage_start", "run-1", "/root", "task-1", 3, "balanced");
        var row = new SingleEventRow(evt);

        var allText = HostAndCollectVisibleText(row);

        Assert.Contains(allText, t => t != null && t.Contains("12:34:56", StringComparison.Ordinal));
    }

    /// <summary>
    /// A collapsed heartbeat group row renders its <b>newest</b> member's
    /// timestamp (the member exposed through
    /// <see cref="HeartbeatGroupRow.Event"/>), so the group header shows the time
    /// of the most recent heartbeat.
    /// </summary>
    [AvaloniaFact]
    public void HeartbeatGroupRow_RendersNewestMemberTimestampAsInvariantTime()
    {
        var newest = new RelayEvent(
            new DateTimeOffset(new DateTime(2026, 8, 20, 9, 15, 7, DateTimeKind.Local)),
            "debug", "watchdog_heartbeat", "run-1", "/root", "task-1", 7, "frontier");
        var older = new RelayEvent(
            new DateTimeOffset(new DateTime(2026, 8, 20, 9, 14, 59, DateTimeKind.Local)),
            "debug", "watchdog_heartbeat", "run-1", "/root", "task-1", 7, "frontier");

        var row = HeartbeatGroupRow.Create(newest, older);

        var allText = HostAndCollectVisibleText(row);

        Assert.Contains(allText, t => t != null && t.Contains("09:15:07", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hosts a <see cref="RunLogView"/> whose <see cref="MainWindowViewModel.Events"/>
    /// contains exactly the given row, then returns every visible text string in
    /// the rendered tree (both <see cref="TextBlock"/> and
    /// <see cref="SelectableTextBlock"/>).
    /// </summary>
    private static List<string?> HostAndCollectVisibleText(IRunLogRow row)
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
            .Where(tb => tb.IsVisible)
            .Select(tb => tb.Text)
            .Concat(runLogView.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Where(stb => stb.IsVisible)
                .Select(stb => stb.Text))
            .ToList();
    }
}
