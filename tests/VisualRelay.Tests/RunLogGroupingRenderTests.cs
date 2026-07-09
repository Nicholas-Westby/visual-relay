using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.ViewModels.RunLogRows;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed class RunLogGroupingRenderTests
{
    /// <summary>
    /// When a <see cref="HeartbeatGroupRow"/> with count &gt; 1 is the data
    /// context, the rendered Run Log shows the count indicator (e.g. "×30") in
    /// the header line.  This test constructs the <see cref="RunLogView"/>
    /// control directly (no <see cref="MainWindow"/> boot) and inspects the
    /// visual tree.
    /// </summary>
    [AvaloniaFact]
    public void GroupedRow_RendersCountInHeader()
    {
        var group = HeartbeatGroupRow.Create(
            MakeHeartbeat(7, "frontier", "silenceMs=30000 deadlineMs=90000"),
            MakeHeartbeat(7, "frontier", "silenceMs=29000 deadlineMs=89000"),
            MakeHeartbeat(7, "frontier", "silenceMs=28000 deadlineMs=88000"),
            MakeHeartbeat(7, "frontier", "silenceMs=27000 deadlineMs=87000"),
            MakeHeartbeat(7, "frontier", "silenceMs=26000 deadlineMs=86000"),
            MakeHeartbeat(7, "frontier", "silenceMs=25000 deadlineMs=85000"),
            MakeHeartbeat(7, "frontier", "silenceMs=24000 deadlineMs=84000"),
            MakeHeartbeat(7, "frontier", "silenceMs=23000 deadlineMs=83000"),
            MakeHeartbeat(7, "frontier", "silenceMs=22000 deadlineMs=82000"),
            MakeHeartbeat(7, "frontier", "silenceMs=21000 deadlineMs=81000"),
            MakeHeartbeat(7, "frontier", "silenceMs=20000 deadlineMs=80000"),
            MakeHeartbeat(7, "frontier", "silenceMs=19000 deadlineMs=79000"),
            MakeHeartbeat(7, "frontier", "silenceMs=18000 deadlineMs=78000"),
            MakeHeartbeat(7, "frontier", "silenceMs=17000 deadlineMs=77000"),
            MakeHeartbeat(7, "frontier", "silenceMs=16000 deadlineMs=76000"),
            MakeHeartbeat(7, "frontier", "silenceMs=15000 deadlineMs=75000"),
            MakeHeartbeat(7, "frontier", "silenceMs=14000 deadlineMs=74000"),
            MakeHeartbeat(7, "frontier", "silenceMs=13000 deadlineMs=73000"),
            MakeHeartbeat(7, "frontier", "silenceMs=12000 deadlineMs=72000"),
            MakeHeartbeat(7, "frontier", "silenceMs=11000 deadlineMs=71000"),
            MakeHeartbeat(7, "frontier", "silenceMs=10000 deadlineMs=70000"),
            MakeHeartbeat(7, "frontier", "silenceMs=9000 deadlineMs=69000"),
            MakeHeartbeat(7, "frontier", "silenceMs=8000 deadlineMs=68000"),
            MakeHeartbeat(7, "frontier", "silenceMs=7000 deadlineMs=67000"),
            MakeHeartbeat(7, "frontier", "silenceMs=6000 deadlineMs=66000"),
            MakeHeartbeat(7, "frontier", "silenceMs=5000 deadlineMs=65000"),
            MakeHeartbeat(7, "frontier", "silenceMs=4000 deadlineMs=64000"),
            MakeHeartbeat(7, "frontier", "silenceMs=3000 deadlineMs=63000"),
            MakeHeartbeat(7, "frontier", "silenceMs=2000 deadlineMs=62000"),
            MakeHeartbeat(7, "frontier", "silenceMs=1000 deadlineMs=61000")
        );

        Assert.True(group.IsGroup);
        Assert.Equal(30, group.Count);

        var vm = new MainWindowViewModel(
            new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        vm.Events.Add(group);

        var runLogView = new RunLogView { DataContext = vm };
        var host = new Window { Content = runLogView, Width = 600, Height = 400 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var allText = runLogView.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.IsVisible)
            .Select(tb => tb.Text)
            .Concat(runLogView.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Where(stb => stb.IsVisible)
                .Select(stb => stb.Text))
            .ToList();

        // The header must contain the count indicator
        Assert.Contains(allText, t => t != null && t.Contains("×30", StringComparison.Ordinal));

        // The header must also contain the shared display line
        Assert.Contains(allText, t => t != null && t.Contains("s7/frontier watchdog_heartbeat", StringComparison.Ordinal));
    }

    /// <summary>
    /// A non-grouped heartbeat (count = 1) renders without a count indicator
    /// — it looks like a plain single row.
    /// </summary>
    [AvaloniaFact]
    public void SingleHeartbeatRow_RendersWithoutCountIndicator()
    {
        var single = new SingleEventRow(
            MakeHeartbeat(7, "frontier", "silenceMs=1000 deadlineMs=61000"));

        Assert.False(single.IsGroup);
        Assert.Equal(1, single.Count);

        var vm = new MainWindowViewModel(
            new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        vm.Events.Add(single);

        var runLogView = new RunLogView { DataContext = vm };
        var host = new Window { Content = runLogView, Width = 600, Height = 400 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var allText = runLogView.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.IsVisible)
            .Select(tb => tb.Text)
            .Concat(runLogView.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Where(stb => stb.IsVisible)
                .Select(stb => stb.Text))
            .ToList();

        // Must contain the display line
        Assert.Contains(allText, t => t != null && t.Contains("s7/frontier watchdog_heartbeat", StringComparison.Ordinal));

        // Must NOT contain a count indicator (no "×N")
        Assert.DoesNotContain(allText, t => t != null && t.Contains("×", StringComparison.Ordinal));
    }

    /// <summary>
    /// A non-heartbeat event row renders identically to today's single row
    /// — no count, no expander, plain DisplayLine + DetailLine.
    /// </summary>
    [AvaloniaFact]
    public void NonHeartbeatRow_RendersPlainHeader()
    {
        var evt = new RelayEvent(
            DateTimeOffset.UtcNow, "info", "stage_start", "run-1", "/root",
            "task-1", 5, "balanced");
        var row = new SingleEventRow(evt);

        var vm = new MainWindowViewModel(
            new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        vm.Events.Add(row);

        var runLogView = new RunLogView { DataContext = vm };
        var host = new Window { Content = runLogView, Width = 600, Height = 400 };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var allText = runLogView.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.IsVisible)
            .Select(tb => tb.Text)
            .Concat(runLogView.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Where(stb => stb.IsVisible)
                .Select(stb => stb.Text))
            .ToList();

        Assert.Contains(allText, t => t != null && t.Contains("s5/balanced stage_start", StringComparison.Ordinal));
        // No count indicator
        Assert.DoesNotContain(allText, t => t != null && t.Contains("×", StringComparison.Ordinal));
    }

    private static RelayEvent MakeHeartbeat(int stageNumber, string tier, string message) =>
        new(
            DateTimeOffset.UtcNow,
            "debug",
            "watchdog_heartbeat",
            "run-1",
            "/root",
            "task-1",
            stageNumber,
            tier,
            Data: new Dictionary<string, string> { ["message"] = message });
}
