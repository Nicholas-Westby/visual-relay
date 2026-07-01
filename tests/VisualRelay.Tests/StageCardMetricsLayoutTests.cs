using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the STAGES board metrics-line truncation defect.
///
/// Root cause: each stage card is a fixed 165 px button whose metrics TextBlock
/// used MaxLines="1" + TextTrimming="CharacterEllipsis". A completed card's
/// metrics line (cost + turns + test, plus the leading duration token it used to
/// carry) overflowed 165 px and was cut with an unrecoverable "…", hiding the
/// cost/turns/test tail.
///
/// Fix: the duration moved onto the status row ("Completed in 1m 12s") and the
/// metrics TextBlock no longer trims (it wraps), so the full cost/turns/test is
/// always readable on every card — Verify and every other stage.
/// </summary>
[Collection("Headless")]
public sealed class StageCardMetricsLayoutTests
{
    // Stage 10 "Fix-verify" — a NON-Verify card with a long name and a
    // multi-minute duration, to prove the fix is general, not Verify-only.
    private const int LongStageNumber = 10;

    [AvaloniaFact]
    public void MetricsTextBlock_LongMetrics_NotTrimmedAndFullyVisible()
    {
        var viewModel = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var stage = viewModel.Stages.Single(s => s.Number == LongStageNumber);

        // Drive the card into a long-metrics completed state: a multi-minute
        // duration + cost + turns + test — the worst realistic case.
        stage.ApplyMetric(new StageRunMetric(
            StageNumber: LongStageNumber, StageName: "Fix-verify", Tier: "balanced",
            Model: "claude", Timestamp: DateTimeOffset.UtcNow, DurationSeconds: 754,
            CostUsd: 0.0029, Priced: true, PromptTokens: 0, CachedTokens: 0,
            OutputTokens: 0, CacheWriteTokens: 0, ReportPath: "/tmp/r.md",
            TraceDirectory: null, Turns: 14));
        stage.SetTestDurationSeconds(187);
        stage.Status = "Done";

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var metricsBlock = FindMetricsTextBlock(window, stage.MetricLabel);
        Assert.NotNull(metricsBlock);

        // (a) The metrics line must NOT trim with an ellipsis — its full text
        //     stays recoverable rather than being cut to "… test 1…".
        Assert.Equal(TextTrimming.None, metricsBlock!.TextTrimming);

        // (b) Nothing is clipped horizontally: the arranged width is enough for
        //     the content's unconstrained desired width (measured infinitely).
        metricsBlock.Measure(Size.Infinity);
        var desiredWidth = metricsBlock.DesiredSize.Width;
        Assert.True(
            metricsBlock.Bounds.Width >= desiredWidth - 1.0 || metricsBlock.TextWrapping == TextWrapping.Wrap,
            $"Metrics line is clipped: arranged width {metricsBlock.Bounds.Width:F1} px "
            + $"< desired {desiredWidth:F1} px and it does not wrap.");

        // (c) Every metric token is present in the rendered text — none dropped.
        var text = metricsBlock.Text ?? string.Empty;
        Assert.Contains("$0.0029", text, StringComparison.Ordinal);
        Assert.Contains("14t", text, StringComparison.Ordinal);
        Assert.Contains("test 3m 07s", text, StringComparison.Ordinal);
        Assert.DoesNotContain("…", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void StatusTextBlock_CompletedStage_ShowsDurationAndDoesNotTrim()
    {
        var viewModel = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var stage = viewModel.Stages.Single(s => s.Number == LongStageNumber);
        stage.ApplyMetric(new StageRunMetric(
            StageNumber: LongStageNumber, StageName: "Fix-verify", Tier: "balanced",
            Model: "claude", Timestamp: DateTimeOffset.UtcNow, DurationSeconds: 754,
            CostUsd: 0.0129, Priced: true, PromptTokens: 0, CachedTokens: 0,
            OutputTokens: 0, CacheWriteTokens: 0, ReportPath: "/tmp/r.md",
            TraceDirectory: null, Turns: 14));
        stage.Status = "Done";

        var window = new MainWindow
        {
            DataContext = viewModel,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The status row now carries the duration; it must show it in full.
        var statusBlock = FindTextBlockWithText(window, stage.StatusLabel);
        Assert.NotNull(statusBlock);
        Assert.Equal(TextTrimming.None, statusBlock!.TextTrimming);
        Assert.Contains("12m 34s", statusBlock.Text ?? string.Empty, StringComparison.Ordinal);
    }

    private static TextBlock? FindMetricsTextBlock(Visual root, string metricLabel) =>
        FindTextBlockWithText(root, metricLabel);

    private static TextBlock? FindTextBlockWithText(Visual root, string text) =>
        root.GetVisualDescendants()
            .OfType<StageBoard>()
            .SelectMany(board => board.GetVisualDescendants().OfType<TextBlock>())
            .FirstOrDefault(tb => tb.Text == text);
}
