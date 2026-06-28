using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The active (Running) stage card must show a live, per-second-ticking elapsed
/// label (e.g. "2m 25s") so a working task does not look frozen between the
/// ~60s-apart watchdog heartbeats. The elapsed/duration is surfaced on the
/// status row (StatusLabel: "Running 2m 25s" / "Completed in 1m 12s") so the
/// fixed-width metrics line is free for cost/turns/test. The per-tick refresh is
/// extracted into <see cref="MainWindowViewModel.UpdateRunningElapsedLabels"/> so
/// the test can seed a past start time and assert the label reflects it without
/// sleeping. Uses the headless harness because StageRowViewModel/
/// MainWindowViewModel touch Avalonia brushes during construction.
/// </summary>
[Collection("Headless")]
public sealed class RunningStageElapsedTests
{
    [AvaloniaFact]
    public void RunningStage_ElapsedLabel_TicksFromStageStart()
    {
        var stage = new StageRowViewModel(RelayStages.All[5]);
        stage.MarkRunning(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(145));

        stage.RefreshElapsed(DateTimeOffset.UtcNow);

        Assert.Equal("2m 25s", stage.ElapsedLabel);
        // The live elapsed shows on the status row so the card visibly ticks.
        Assert.Contains("2m 25s", stage.StatusLabel);
    }

    [AvaloniaFact]
    public void NonRunningStage_ElapsedLabel_IsUntouchedByTick()
    {
        var stage = new StageRowViewModel(RelayStages.All[5]);

        stage.RefreshElapsed(DateTimeOffset.UtcNow);

        Assert.Equal(string.Empty, stage.ElapsedLabel);
        // A Waiting stage never leaks an elapsed value: no metrics, plain status.
        Assert.Equal(string.Empty, stage.MetricLabel);
        Assert.Equal("Waiting", stage.StatusLabel);
    }

    [AvaloniaFact]
    public void StageDone_FinalDuration_IsNotOverwrittenByTick()
    {
        var stage = new StageRowViewModel(RelayStages.All[5]);
        stage.MarkRunning(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30));

        // Stage completes: a final metric is applied and the stage leaves Running.
        // DurationSeconds 72 ⇒ DurationLabel "1m 12s".
        stage.ApplyMetric(new StageRunMetric(
            StageNumber: 6, StageName: "Implement", Tier: "balanced", Model: "claude",
            Timestamp: DateTimeOffset.UtcNow, DurationSeconds: 72, CostUsd: 0.05, Priced: true,
            PromptTokens: 0, CachedTokens: 0, OutputTokens: 0, CacheWriteTokens: 0,
            ReportPath: "/tmp/r.md", TraceDirectory: null, Turns: 4));
        stage.Status = "Done";

        // A late tick must not clobber the recorded final duration.
        stage.RefreshElapsed(DateTimeOffset.UtcNow);

        // The final duration is shown on the status row ("Completed in 1m 12s"),
        // and the stale running elapsed ("0m 30s") must not survive anywhere.
        Assert.Equal("Completed in 1m 12s", stage.StatusLabel);
        Assert.DoesNotContain("0m 30s", stage.StatusLabel);
        Assert.DoesNotContain("0m 30s", stage.MetricLabel);
    }

    [AvaloniaFact]
    public async Task TimerTick_RefreshesCurrentlyRunningStageCard()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var stage = viewModel.Stages.First(s => s.Number == 6);
        stage.MarkRunning(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(65));

        // The method the 1-second DispatcherTimer invokes — call it directly.
        viewModel.UpdateRunningElapsedLabels();

        Assert.Equal("1m 05s", stage.ElapsedLabel);
        // Sibling non-running stages stay clean.
        Assert.Equal(string.Empty, viewModel.Stages.First(s => s.Number == 1).ElapsedLabel);
    }

    /// <summary>
    /// When a stage_start event is replayed (e.g. during task switch via
    /// LoadRunHistoryAsync), ApplyStageEventToBoard must use the event's
    /// original Timestamp — not DateTimeOffset.UtcNow — so the elapsed timer
    /// reflects the real stage start time rather than resetting to ~0s on every
    /// replay. This test fires a stage_start with a past timestamp through the
    /// same HandleRelayEvent → ApplyStageEventToBoard path that task-switch
    /// replay uses, then ticks the elapsed timer to verify the label preserves
    /// the original elapsed.
    /// </summary>
    [AvaloniaFact]
    public async Task ReplayedStageStart_PreservesOriginalTimestamp()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        // Simulate a stage_start event emitted 5 minutes ago.
        var pastTimestamp = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(300);
        var stageStartEvent = new RelayEvent(
            Timestamp: pastTimestamp,
            Level: "info",
            EventName: "stage_start",
            RunId: "test-run",
            RootPath: repo.Root,
            TaskId: "alpha",
            StageNumber: 1,
            Tier: "cheap");

        // Route through HandleRelayEvent (the same entry point used by live
        // events and by LoadRunHistoryAsync replay).
        var handleMethod = typeof(MainWindowViewModel)
            .GetMethod("HandleRelayEvent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        handleMethod.Invoke(viewModel, [stageStartEvent]);

        // Tick the 1-second timer to refresh elapsed labels.
        viewModel.UpdateRunningElapsedLabels();

        var stage = viewModel.Stages.First(s => s.Number == 1);
        // Bug: MarkRunning(DateTimeOffset.UtcNow) makes the elapsed ~0s ("0s").
        // Fix: MarkRunning(relayEvent.Timestamp) preserves the ~5m elapsed.
        Assert.NotEqual("0s", stage.ElapsedLabel);
        Assert.Contains("m", stage.ElapsedLabel);
        Assert.Contains(stage.ElapsedLabel, stage.StatusLabel);
    }
}
