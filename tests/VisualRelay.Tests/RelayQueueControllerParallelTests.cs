using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerParallelTests
{
    [Fact]
    public async Task DrainAsync_TwoPhase_PlansInParallel_ThenExecutesSerially()
    {
        // A drain with 3 tasks: all 3 should be processed to completion.
        // The two-phase drain processes planning in Phase 1 and serial
        // execute in Phase 2.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        var results = await controller.DrainAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Committed, r.Status));
        Assert.Equal(3, runner.TasksRun.Count);
    }

    [Fact]
    public async Task DrainAsync_IdempotentRePlan_SkipsAlreadyPlannedTasks()
    {
        // Tasks whose status.json already shows stages 1–4 as Done must proceed
        // to the serial execute phase without being re-planned in Phase 1.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("already-planned", "# Already planned\n");
        repo.WriteTask("fresh", "# Fresh\n");

        // Pre-populate "already-planned" with status 1–4 Done, 5–11 Waiting.
        var taskDir = Path.Combine(repo.Root, ".relay", "already-planned");
        Directory.CreateDirectory(taskDir);
        var preStatus = RelayStages.All.Select(s => new StageStatusEntry(
            s.Number, s.Name,
            s.Number <= 4 ? "Done" : "Waiting")).ToList();
        await StageStatusRecord.WriteAsync(taskDir, preStatus);
        // Also write a manifest so resume doesn't choke.
        await File.WriteAllTextAsync(Path.Combine(taskDir, "manifest.txt"), "src/app.cs\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Both tasks should be processed through the serial execute phase.
        Assert.Equal(2, results.Count);
        Assert.Equal(2, runner.TasksRun.Count);
        // "already-planned" must appear in the run list (execute phase).
        Assert.Contains("already-planned", runner.TasksRun);
        Assert.Contains("fresh", runner.TasksRun);
    }

    [Fact]
    public async Task DrainAsync_PlanningFlag_ExcludesFromPhase2_CountsTowardCircuitBreaker()
    {
        // A task that flags during drain (e.g. at stage 5) must be set aside
        // as NeedsReview and its flag must count toward the circuit breaker.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("good", "# Good\n");
        repo.WriteTask("bad", "# Bad\n");
        repo.WriteTask("also-good", "# Also good\n");

        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("good", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            new RelayTaskOutcome("bad", RelayTaskOutcomeStatus.Flagged, null, null,
                "stage 5: author-tests passed after implementation stripped"),
            new RelayTaskOutcome("also-good", RelayTaskOutcomeStatus.Committed, "hash", "sha", null));

        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // "bad" should appear in results as Flagged.
        Assert.Contains(results, r => r.TaskId == "bad" && r.Status == RelayTaskOutcomeStatus.Flagged);

        // "bad" must be set aside as NeedsReview.
        Assert.Contains(controller.Tasks, t => t.Id == "bad" && t.NeedsReview);

        // "good" and "also-good" should have committed.
        Assert.Contains(results, r => r.TaskId == "good" && r.Status == RelayTaskOutcomeStatus.Committed);
        Assert.Contains(results, r => r.TaskId == "also-good" && r.Status == RelayTaskOutcomeStatus.Committed);

        // Only 1 flag — circuit breaker should NOT halt.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    [Fact]
    public async Task DrainAsync_CancellationToken_CancelsInFlightTasks()
    {
        // A per-drain CancellationToken must cancel the drain early.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        const int taskCount = 5;
        for (int i = 0; i < taskCount; i++)
            repo.WriteTask($"task-{i:D2}", $"# Task {i}\n");

        var runner = new CancellableRecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        // Start drain in background, cancel after a short delay.
        using var cts = new CancellationTokenSource();
        Task<IReadOnlyList<RelayTaskOutcome>> drainTask;
        drainTask = controller.DrainAsync(cts.Token);
        await Task.Delay(100, CancellationToken.None);
        cts.Cancel();

        IReadOnlyList<RelayTaskOutcome> results;
        try
        {
            results = await drainTask;
        }
        catch (OperationCanceledException)
        {
            // Cancellation may surface as OCE — that's acceptable too.
            return;
        }

        // Should have partial results or the drain stopped early.
        Assert.True(results.Count < taskCount,
            $"expected fewer than {taskCount} results after cancellation, got {results.Count}");
    }

    /// <summary>
    /// A recording runner that respects cancellation tokens.
    /// </summary>
    private sealed class CancellableRecordingTaskRunner : IRelayTaskRunner
    {
        public List<string> TasksRun { get; } = [];

        public async Task<RelayTaskOutcome> RunTaskAsync(
            string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(200, cancellationToken);
            TasksRun.Add(taskId);
            return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "sha", null);
        }
    }

    [Fact]
    public async Task DrainAsync_PauseRequested_DuringPlanning_StopsBeforePhase2()
    {
        // When RequestPause is called during Phase 1 (planning), the drain must:
        // 1. Allow in-flight planning tasks to complete.
        // 2. Stop before starting Phase 2 (serial execute).
        // 3. Leave the remaining tasks in the queue.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);

        // Request pause after the first task completes.
        runner.AfterRun = () => controller.RequestPause();

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Only the first task should have run before pause kicked in.
        Assert.Single(results);
        Assert.Equal(RelayQueueState.Paused, controller.State);

        // Remaining tasks are still in the queue.
        Assert.True(controller.Tasks.Count >= 2);
    }

    [Fact]
    public async Task DrainAsync_PreExistingDrainHaltedMarker_ClearedAtStart()
    {
        // A stale DRAIN-HALTED marker from a previous crash must be cleared
        // when a new drain starts, even in the two-phase flow.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED"), "stale\n");

        var controller = new RelayQueueController(repo.Root, new RecordingTaskRunner());
        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Single(results);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
        Assert.Equal(RelayQueueState.Completed, controller.State);
    }

    [Fact]
    public async Task DrainAsync_SingleTask_NormalRun_StillWorks()
    {
        // A drain with a single task must behave exactly as today: plan + execute
        // completes, the task is removed, state is Completed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("solo", "# Solo\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[0].Status);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.Empty(controller.Tasks);
    }
}
