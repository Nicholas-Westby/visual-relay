using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using static VisualRelay.Tests.RelayEventTestDispatch;

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

    /// <summary>
    /// Problem A: a stage that runs multiple attempts (the Fix-verify loop emits a
    /// stage_start per attempt) must show the CUMULATIVE time across attempts, not
    /// re-anchor to the latest attempt's start. Drives attempt 1 (reports 300 s,
    /// done) then attempt 2 (just started) through the live event path and ticks.
    /// </summary>
    [AvaloniaFact]
    public async Task RetriedStage_RunningTimer_AccumulatesAcrossAttempts_NotPerAttemptReset()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var now = DateTimeOffset.UtcNow;
        // Attempt 1: ran 300 s, completed.
        Dispatch(viewModel, StageStart("alpha", 10, now - TimeSpan.FromSeconds(400)));
        Dispatch(viewModel, StageDone("alpha", 10, now - TimeSpan.FromSeconds(100), seconds: 300));
        // Attempt 2 (retry): started ~7 s ago, still running.
        Dispatch(viewModel, StageStart("alpha", 10, now - TimeSpan.FromSeconds(7)));

        viewModel.UpdateRunningElapsedLabels();

        var stage = viewModel.Stages.First(s => s.Number == 10);
        // Cumulative ≈ 300 s banked + ~7 s live ⇒ "5m 0Xs". The per-attempt reset
        // bug would show only attempt 2's ~7 s.
        Assert.StartsWith("5m", stage.ElapsedLabel);
        Assert.NotEqual("7s", stage.ElapsedLabel);
        Assert.StartsWith("Running 5m", stage.StatusLabel);
    }

    /// <summary>
    /// Problem A (completed): once a retried stage finally goes green, its recorded
    /// duration must be the SUM of all attempts, not just the last attempt — so the
    /// done card reconciles with the cumulative running timer it just replaced.
    /// </summary>
    [AvaloniaFact]
    public async Task RetriedStage_DoneDuration_IsCumulativeAcrossAttempts()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        var t = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        Dispatch(viewModel, StageStart("alpha", 10, t));
        Dispatch(viewModel, StageDone("alpha", 10, t.AddSeconds(180), seconds: 180)); // attempt 1: 3m
        Dispatch(viewModel, StageStart("alpha", 10, t.AddSeconds(180)));
        Dispatch(viewModel, StageDone("alpha", 10, t.AddSeconds(300), seconds: 120)); // attempt 2: 2m, green

        var stage = viewModel.Stages.First(s => s.Number == 10);
        // 180 + 120 = 300 s. The bug shows only the last attempt ("2m 00s").
        Assert.Equal("Completed in 5m 00s", stage.StatusLabel);
    }
}
