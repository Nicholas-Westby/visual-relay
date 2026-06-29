using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;
using static VisualRelay.Tests.RelayEventTestDispatch;

namespace VisualRelay.Tests;

/// <summary>
/// Pins the queue-row "overall" task timer semantics chosen for Problem B:
/// overall = the SUM of the task's own stage active times (matching the persisted
/// per-task metric, which already sums squashed stage durations), NOT the
/// wall-clock since planning. That excludes the idle queue-wait a task spends
/// parked while ANOTHER task executes during a Run All drain, so the queue row
/// reconciles with the stage cards instead of dwarfing them.
/// </summary>
[Collection("Headless")]
public sealed class QueueRowElapsedOverallTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

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
    public async Task Overall_IsActiveSum_ExcludesIdleQueueWait_AndReconcilesWithStages()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            // gamma is running and selected, so its events drive both the stage
            // board and its overall active-time accumulator.
            viewModel.RestoreRunningTaskState("gamma", stageNumber: null, stageName: null);

            // Stage 1 (planning): 2m active.
            Dispatch(viewModel, StageStart("gamma", 1, Anchor));
            Dispatch(viewModel, StageDone("gamma", 1, Anchor.AddSeconds(120), seconds: 120));

            // A ONE-HOUR idle queue-wait while another task executes: no stage is
            // open for gamma, so nothing accrues to its overall.
            var afterIdle = Anchor.AddSeconds(120).AddHours(1);

            // Stage 5: 3m active.
            Dispatch(viewModel, StageStart("gamma", 5, afterIdle));
            Dispatch(viewModel, StageDone("gamma", 5, afterIdle.AddSeconds(180), seconds: 180));

            // Stage 10 (Fix-verify) retried: 5m + 4m across two attempts = 9m.
            var s10 = afterIdle.AddSeconds(180);
            Dispatch(viewModel, StageStart("gamma", 10, s10));
            Dispatch(viewModel, StageDone("gamma", 10, s10.AddSeconds(300), seconds: 300));
            Dispatch(viewModel, StageStart("gamma", 10, s10.AddSeconds(300)));
            Dispatch(viewModel, StageDone("gamma", 10, s10.AddSeconds(540), seconds: 240));

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // Active sum = 120 + 180 + (300 + 240) = 840 s = 14m 00s. The 1-hour
            // idle queue-wait is NOT counted.
            Assert.Equal("14m 00s", row.RunningElapsedLabel);

            // Reconciliation: overall equals the sum of the per-stage card times,
            // with the retried stage 10 contributing its cumulative 9m.
            var stages = viewModel.Stages;
            var stage10 = stages.First(s => s.Number == 10);
            Assert.Equal("Completed in 9m 00s", stage10.StatusLabel);
            var stageSumSeconds =
                ParseLabelSeconds(StripCompleted(stages.First(s => s.Number == 1).StatusLabel)) +
                ParseLabelSeconds(StripCompleted(stages.First(s => s.Number == 5).StatusLabel)) +
                ParseLabelSeconds(StripCompleted(stage10.StatusLabel));
            Assert.Equal(ParseLabelSeconds(row.RunningElapsedLabel), stageSumSeconds);
        }
    }

    [AvaloniaFact]
    public async Task RunningStage_LiveTick_KeepsOverallReconciledWithStageSum()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            viewModel.RestoreRunningTaskState("gamma", stageNumber: null, stageName: null);

            var now = DateTimeOffset.UtcNow;
            // Stage 5 completed (3m), stage 6 currently running (started 30s ago).
            Dispatch(viewModel, StageStart("gamma", 5, now - TimeSpan.FromSeconds(230)));
            Dispatch(viewModel, StageDone("gamma", 5, now - TimeSpan.FromSeconds(50), seconds: 180));
            Dispatch(viewModel, StageStart("gamma", 6, now - TimeSpan.FromSeconds(30)));

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            var stage6 = viewModel.Stages.First(s => s.Number == 6);
            // Overall = 180 banked + ~30 live = ~210s; stage 6 card live = ~30s.
            // The overall must equal stage 5 (180) + stage 6 live — reconciled.
            var overall = ParseLabelSeconds(row.RunningElapsedLabel);
            var stage6Live = ParseLabelSeconds(stage6.ElapsedLabel);
            Assert.Equal(180 + stage6Live, overall);
        }
    }

    [AvaloniaFact]
    public async Task ExecuteStart_PreservesPlanningPhaseActiveTime()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
            Assert.NotNull(lifecycle.OnPlanningStarted);
            Assert.NotNull(lifecycle.OnExecuteStarted);

            // Planning phase: the accumulator is created at planning start; stage 1
            // (2m) accrues even though the task is not yet in the running set.
            lifecycle.OnPlanningStarted.Invoke("gamma");
            Dispatch(viewModel, StageStart("gamma", 1, Anchor));
            Dispatch(viewModel, StageDone("gamma", 1, Anchor.AddSeconds(120), seconds: 120));
            Dispatcher.UIThread.RunJobs();

            // Execute phase begins (after the idle wait): BeginRunningTask must
            // PRESERVE the planning-phase active time, not reset it.
            lifecycle.OnExecuteStarted.Invoke("gamma");
            Dispatcher.UIThread.RunJobs();

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // 2m of planning active time is preserved (no live stage open yet).
            Assert.Equal("2m 00s", row.RunningElapsedLabel);
        }
    }

    [AvaloniaFact]
    public async Task ExecuteStart_NoPlanningPhase_StartsFreshAccumulator()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            // No OnPlanningStarted (single-run path, or a resume where planning is
            // already done and skipped): execute start creates a fresh accumulator.
            var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
            Assert.NotNull(lifecycle.OnExecuteStarted);
            lifecycle.OnExecuteStarted.Invoke("gamma");
            Dispatcher.UIThread.RunJobs();

            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // No stage segments yet ⇒ 0s (not null/empty: a real accumulator exists).
            Assert.Equal("0s", row.RunningElapsedLabel);
        }
    }

    /// <summary>
    /// Cross-drain regression: a task LEFT PLANNED when a drain pauses must not
    /// carry its planning-phase active time into the resume drain. The post-drain
    /// cleanup drops the stale accumulator so the resume's execute start begins a
    /// fresh active-time count.
    /// </summary>
    [AvaloniaFact]
    public async Task DrainEnd_DropsStaleAccumulator_SoResumeStartsFresh()
    {
        var (viewModel, repo) = await LoadTaskAsync("gamma");
        using (repo)
        {
            var lifecycle = viewModel.CreateDrainLifecycleCallbacks();
            Assert.NotNull(lifecycle.OnPlanningStarted);
            Assert.NotNull(lifecycle.OnPlanningCompleted);
            Assert.NotNull(lifecycle.OnExecuteStarted);

            // Drain 1: gamma plans (2m accrues) then is LEFT PLANNED (paused).
            lifecycle.OnPlanningStarted.Invoke("gamma");
            Dispatch(viewModel, StageStart("gamma", 1, Anchor));
            Dispatch(viewModel, StageDone("gamma", 1, Anchor.AddSeconds(120), seconds: 120));
            lifecycle.OnPlanningCompleted.Invoke("gamma", RelayTaskOutcomeStatus.Planned);
            Dispatcher.UIThread.RunJobs();

            // Drain 1 ends (paused) → cleanup drops the stale accumulator.
            viewModel.DropStaleRunAnchorsAfterDrain();

            // Drain 2 (resume): execute starts directly with a fresh accumulator.
            lifecycle.OnExecuteStarted.Invoke("gamma");
            Dispatcher.UIThread.RunJobs();
            viewModel.UpdateRunningElapsedLabels();

            var row = viewModel.Tasks.First(t => t.Id == "gamma");
            // The 2m of drain-1 planning was dropped; resume starts fresh at 0s.
            Assert.Equal("0s", row.RunningElapsedLabel);
        }
    }

    private static string StripCompleted(string statusLabel) =>
        statusLabel.Replace("Completed in ", "", StringComparison.Ordinal);
}
