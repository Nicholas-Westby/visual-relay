using System.Globalization;
using System.Reflection;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Test helpers that drive <see cref="RelayEvent"/>s through the view-model's
/// private <c>HandleRelayEvent</c> — the same entry point the live
/// <c>ObservableRelayEventSink</c> calls — plus builders for the stage_start /
/// stage_done events the timer/reconciliation tests replay. Reflection mirrors
/// the established pattern in the existing replay test rather than widening the
/// production surface for tests.
/// </summary>
internal static class RelayEventTestDispatch
{
    private static readonly MethodInfo HandleRelayEventMethod = typeof(MainWindowViewModel)
        .GetMethod("HandleRelayEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static void Dispatch(MainWindowViewModel viewModel, RelayEvent relayEvent) =>
        HandleRelayEventMethod.Invoke(viewModel, [relayEvent]);

    public static RelayEvent StageStart(string taskId, int stage, DateTimeOffset at) =>
        new(at, "info", "stage_start", "test-run", "/root", taskId, stage, "balanced",
            Data: new Dictionary<string, string> { ["name"] = $"Stage {stage}" });

    /// <summary>A stage_done carrying both the formatted "time" and the numeric
    /// "timeSeconds" the live event sink now emits (the accumulator banks the
    /// numeric reported duration).</summary>
    public static RelayEvent StageDone(string taskId, int stage, DateTimeOffset at, double seconds) =>
        new(at, "info", "stage_done", "test-run", "/root", taskId, stage, "balanced",
            Data: new Dictionary<string, string>
            {
                ["name"] = $"Stage {stage}",
                ["time"] = FormatDuration(seconds),
                ["timeSeconds"] = seconds.ToString(CultureInfo.InvariantCulture),
                ["cost"] = "$0.01"
            });

    /// <summary>Parses an <see cref="ElapsedFormatter"/>/duration label ("7s",
    /// "1m 04s") back to whole seconds so a test can sum stage cards and compare
    /// to the overall.</summary>
    public static int ParseLabelSeconds(string label)
    {
        label = label.Trim();
        var mIndex = label.IndexOf("m ", StringComparison.Ordinal);
        if (mIndex < 0)
            return int.Parse(label.TrimEnd('s'), CultureInfo.InvariantCulture);
        var minutes = int.Parse(label[..mIndex], CultureInfo.InvariantCulture);
        var seconds = int.Parse(label[(mIndex + 2)..].TrimEnd('s'), CultureInfo.InvariantCulture);
        return minutes * 60 + seconds;
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(0, seconds):0}s";
        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }
}
