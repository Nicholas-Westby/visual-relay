using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerTwoPhaseTests
{
    [Fact]
    public async Task DrainAsync_TwoPhaseConstructor_PlansThenExecutes_AllTasksComplete()
    {
        // When the two-phase constructor is used (planSubagentRunner +
        // planTestRunner non-null), tasks needing planning must go through
        // Phase 1 (planning in worktrees) then Phase 2 (serial execute).
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var planRunner = new ScriptedSubagentRunner();
        planRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var phase2Runner = new RecordingTaskRunner();

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => planRunner,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Equal(2, results.Count);
        // Both should have committed (planning succeeded, then Phase 2 executed).
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Committed, r.Status));
        // Phase 2 runner must have been called for both tasks.
        Assert.Equal(2, phase2Runner.TasksRun.Count);
        Assert.Contains("alpha", phase2Runner.TasksRun);
        Assert.Contains("beta", phase2Runner.TasksRun);
    }

    [Fact]
    public async Task DrainAsync_TwoPhaseConstructor_FlaggedTaskExcludedFromPhase2()
    {
        // A task that flags during planning must NOT proceed to Phase 2.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("good", "# Good\n");
        repo.WriteTask("bad-plan", "# Bad plan\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var goodRunner = new ScriptedSubagentRunner();
        goodRunner.SeedHappyPath("src/good.cs", "tests/good.tests.cs");
        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var phase2Runner = new RecordingTaskRunner();

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: taskId => taskId == "bad-plan" ? flagAt3 : goodRunner,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // "bad-plan" flags at stage 3, "good" goes through planning + execute.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r is { TaskId: "bad-plan", Status: RelayTaskOutcomeStatus.Flagged });
        Assert.Contains(results, r => r is { TaskId: "good", Status: RelayTaskOutcomeStatus.Committed });

        // Phase 2 must only have run "good", not "bad-plan".
        Assert.Single(phase2Runner.TasksRun);
        Assert.Equal("good", phase2Runner.TasksRun[0]);
    }

    [Fact]
    public async Task DrainAsync_TwoPhaseConstructor_AlreadyPlannedSkipsPhase1()
    {
        // A task with stages 1–4 Done in status.json must skip Phase 1 and
        // go directly to Phase 2. The planSubagentRunner must not be called.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("already-planned", "# Already planned\n");
        repo.WriteTask("fresh", "# Fresh\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        // Pre-populate status for "already-planned".
        var taskDir = Path.Combine(repo.Root, ".relay", "already-planned");
        Directory.CreateDirectory(taskDir);
        var preStatus = RelayStages.All.Select(s => new StageStatusEntry(
            s.Number, s.Name, s.Number <= 4 ? "Done" : "Waiting")).ToList();
        await StageStatusRecord.WriteAsync(taskDir, preStatus);
        await File.WriteAllTextAsync(Path.Combine(taskDir, "manifest.txt"), "src/app.cs\n");

        var planRunner = new ScriptedSubagentRunner();
        planRunner.SeedHappyPath("src/fresh.cs", "tests/fresh.tests.cs");
        var phase2Runner = new RecordingTaskRunner();

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => planRunner,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Committed, r.Status));
        Assert.Equal(2, phase2Runner.TasksRun.Count);
        Assert.Contains("already-planned", phase2Runner.TasksRun);
        Assert.Contains("fresh", phase2Runner.TasksRun);
    }

    [Fact]
    public async Task DrainAsync_TwoPhaseConstructor_AllPlanningTasksFlagged_StillReturnsResults()
    {
        // When ALL planning tasks flag, Phase 2 must be skipped entirely
        // and the drain must return results (no crash, no hang).
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-1", "# Bad 1\n");
        repo.WriteTask("bad-2", "# Bad 2\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var flagAt2 = new FlagAtStageSubagentRunner(flagAtStage: 2);
        var phase2Runner = new RecordingTaskRunner();

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => flagAt2,
            planTestRunner: new ScriptedTestRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Flagged, r.Status));
        // Phase 2 must NOT have been called at all.
        Assert.Empty(phase2Runner.TasksRun);
        // Both must be NeedsReview.
        Assert.All(results, r =>
        {
            var marker = Path.Combine(repo.Root, ".relay", r.TaskId, "NEEDS-REVIEW");
            Assert.True(File.Exists(marker));
        });
    }

    [Fact]
    public async Task DrainAsync_TwoPhaseConstructor_LifecycleCallbacks_AreInvoked()
    {
        // Lifecycle callbacks must be invoked at the correct boundaries.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-1", "# Task 1\n");
        repo.WriteTask("task-2", "# Task 2\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var planRunner = new ScriptedSubagentRunner();
        planRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var phase2Runner = new RecordingTaskRunner();

        var planningStarted = new List<string>();
        var planningCompleted = new List<string>();
        var executeStarted = new List<string>();
        var executeCompleted = new List<string>();

        var lifecycle = new DrainLifecycleCallbacks
        {
            OnPlanningStarted = planningStarted.Add,
            OnPlanningCompleted = (id, _) => planningCompleted.Add(id),
            OnExecuteStarted = executeStarted.Add,
            OnExecuteCompleted = (id, _) => executeCompleted.Add(id)
        };

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => planRunner,
            planTestRunner: new ScriptedTestRunner(),
            lifecycle: lifecycle);

        await controller.RefreshAsync();
        await controller.DrainAsync();

        // Both tasks must have entered and exited planning.
        Assert.Equal(2, planningStarted.Count);
        Assert.Contains("task-1", planningStarted);
        Assert.Contains("task-2", planningStarted);
        Assert.Equal(2, planningCompleted.Count);

        // Both tasks must have entered and exited execution.
        Assert.Equal(2, executeStarted.Count);
        Assert.Contains("task-1", executeStarted);
        Assert.Contains("task-2", executeStarted);
        Assert.Equal(2, executeCompleted.Count);
    }

}
