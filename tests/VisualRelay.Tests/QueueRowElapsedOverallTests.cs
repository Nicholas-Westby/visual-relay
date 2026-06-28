using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Pins that the LEFT queue list's running-task row shows the OVERALL
/// wall-clock elapsed (since the task's pipeline began, planning stages
/// 1–4 included), NOT the current stage's per-stage elapsed. The drain
/// fires OnPlanningStarted for all planning tasks together; execute is
/// serial, so a later-executing task's overall elapsed includes its idle
/// queue wait — consistent with the "overall wall-clock since pipeline
/// began" framing.
/// </summary>
[Collection("Headless")]
public sealed class QueueRowElapsedOverallTests
{
    private static async Task<(MainWindowViewModel ViewModel, TestRepository Repo)> LoadTaskAsync(string taskId)
    {
        var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask(taskId, $"# {taskId}\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        return (viewModel, repo);
    }

    [AvaloniaFact]
    public async Task Drain_RunningTaskElapsed_ShowsOverallTime_NotStageTime()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            viewModel.RestoreRunningTaskState("gamma", stageNumber: 5, stageName: "Author-tests");

            var utcNow = DateTimeOffset.UtcNow;
            // Seed the run-start anchor 10 minutes in the past (overall start).
            viewModel.SetRunStartedAt("gamma", utcNow - TimeSpan.FromMinutes(10));
            // Mark the Author-tests stage running 4 min 46 s ago (per-stage start).
            var authorTests = viewModel.Stages.First(s => s.Number == 5);
            authorTests.MarkRunning(utcNow - TimeSpan.FromSeconds(286));

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // Overall elapsed: 10 min.
            Assert.Equal("10m 00s", row.RunningElapsedLabel);
            Assert.Contains("10m 00s", row.MetricsLine);
            // Stage elapsed: 4 min 46 s.
            Assert.Equal("4m 46s", authorTests.ElapsedLabel);
            // The queue row must NOT mirror the stage card's per-stage elapsed.
            Assert.NotEqual(row.RunningElapsedLabel, authorTests.ElapsedLabel);
        }
    }

    [AvaloniaFact]
    public async Task BeginRunningTask_PreservesExistingPlanningAnchor()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            viewModel.RestoreRunningTaskState("gamma", stageNumber: 5, stageName: "Author-tests");

            var utcNow = DateTimeOffset.UtcNow;
            // Seed a 10-min-ago planning-start anchor (simulating that
            // OnPlanningStarted captured the overall start 10 min ago).
            viewModel.SetRunStartedAt("gamma", utcNow - TimeSpan.FromMinutes(10));

            // Now the drain's Phase 2 begins: OnExecuteStarted fires, which
            // calls BeginRunningTask. With the fix, TryAdd *preserves* the
            // seeded planning-start anchor; before the fix, the unconditional
            // indexer assignment overwrites it to UtcNow (~0s ago).
            var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
            Assert.NotNull(lifecycle.OnExecuteStarted);
            lifecycle.OnExecuteStarted.Invoke("gamma");
            Dispatcher.UIThread.RunJobs();

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            Assert.Equal("10m 00s", row.RunningElapsedLabel);
        }
    }

    [AvaloniaFact]
    public async Task BeginRunningTask_CapturesStart_WhenNoPriorAnchor()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            viewModel.RestoreRunningTaskState("gamma", stageNumber: 5, stageName: "Author-tests");

            // Do NOT seed _runStartedAt — this simulates a task skipping
            // planning (stages 1–4 already Done → straight to execute), or
            // the single-run path (RunOneAsync calls BeginRunningTask once
            // with no prior entry).
            var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
            Assert.NotNull(lifecycle.OnExecuteStarted);
            lifecycle.OnExecuteStarted.Invoke("gamma");
            Dispatcher.UIThread.RunJobs();

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // TryAdd succeeds when there's no prior entry, so the label must
            // NOT be null/empty — a real start was captured.
            Assert.False(string.IsNullOrEmpty(row.RunningElapsedLabel));
            // With no backdated anchor, the elapsed should be near-zero
            // ("0s"), not the 10-min-ago value from the other tests.
            Assert.Equal("0s", row.RunningElapsedLabel);
        }
    }

    /// <summary>
    /// Cross-drain regression: a task LEFT PLANNED when a drain pauses must not carry
    /// its planning-start anchor into the resume drain. The post-drain cleanup
    /// (DropStaleRunAnchorsAfterDrain) drops the stale anchor so the resume's execute
    /// start re-anchors — the queue-row elapsed reflects the RESUME, not the original
    /// planning inflated by the pause gap.
    /// </summary>
    [AvaloniaFact]
    public async Task DrainEnd_DropsStalePlannedAnchor_SoResumeReanchorsAtExecuteStart()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
            Assert.NotNull(lifecycle.OnPlanningStarted);
            Assert.NotNull(lifecycle.OnPlanningCompleted);
            Assert.NotNull(lifecycle.OnExecuteStarted);

            // Drain 1: gamma plans, then is LEFT PLANNED (drain pauses before executing
            // it). OnPlanningStarted seeds the anchor; OnPlanningCompleted (non-flagged)
            // marks it Planned and KEEPS the anchor.
            lifecycle.OnPlanningStarted.Invoke("gamma");
            lifecycle.OnPlanningCompleted.Invoke("gamma", RelayTaskOutcomeStatus.Planned);
            Dispatcher.UIThread.RunJobs();

            // Simulate the pause gap: backdate the retained planning anchor 10 minutes.
            viewModel.SetRunStartedAt("gamma", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

            // Drain 1 ends (paused) → post-drain cleanup drops the stale anchor.
            viewModel.DropStaleRunAnchorsAfterDrain();

            // Drain 2 (resume): planning is skipped (1–4 Done) so OnPlanningStarted
            // never fires; execute starts directly and re-anchors via TryAdd.
            lifecycle.OnExecuteStarted.Invoke("gamma");
            Dispatcher.UIThread.RunJobs();

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // The anchor reflects the RESUME (~0s), not the 10-min-old planning start.
            Assert.Equal("0s", row.RunningElapsedLabel);
        }
    }
}
