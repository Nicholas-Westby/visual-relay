using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;
using VisualRelay.DrainQueue;

namespace VisualRelay.Tests;

// Controller integration tests, split out of DrainQueueToolTests.cs to keep
// each file under the 300-line guard.
public sealed partial class DrainQueueToolTests
{
    // ═══════════════════════════════════════════════════════════════
    // Controller integration: task-subset ordering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DrainAsync_TaskSubset_DrainsOnlyRequestedTasks()
    {
        // When Tasks is rewritten to a subset after RefreshAsync,
        // DrainAsync must only drain those tasks in the given order.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        // Simulate the tool's subset selection: clear Tasks and add only
        // the requested subset in the requested order.
        controller.Tasks.Clear();
        controller.Tasks.Add(new RelayTaskItem(
            "gamma", Path.Combine(repo.Root, "llm-tasks", "gamma.md"),
            Path.Combine(repo.Root, ".relay", "gamma"), false, []));
        controller.Tasks.Add(new RelayTaskItem(
            "alpha", Path.Combine(repo.Root, "llm-tasks", "alpha.md"),
            Path.Combine(repo.Root, ".relay", "alpha"), false, []));

        var results = await controller.DrainAsync();

        Assert.Equal(2, results.Count);
        // Must drain in the order the Tasks were added: gamma first, then alpha.
        Assert.Equal("gamma", results[0].TaskId);
        Assert.Equal("alpha", results[1].TaskId);
        Assert.Equal(["gamma", "alpha"], runner.TasksRun);
    }

    [Fact]
    public async Task DrainAsync_TaskSubset_WithReversedOrder_DrainsInReversedOrder()
    {
        // The tool must preserve the order the user specifies on the
        // command line (not sort alphabetically).
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        controller.Tasks.Clear();
        controller.Tasks.Add(new RelayTaskItem(
            "beta", Path.Combine(repo.Root, "llm-tasks", "beta.md"),
            Path.Combine(repo.Root, ".relay", "beta"), false, []));
        controller.Tasks.Add(new RelayTaskItem(
            "gamma", Path.Combine(repo.Root, "llm-tasks", "gamma.md"),
            Path.Combine(repo.Root, ".relay", "gamma"), false, []));
        controller.Tasks.Add(new RelayTaskItem(
            "alpha", Path.Combine(repo.Root, "llm-tasks", "alpha.md"),
            Path.Combine(repo.Root, ".relay", "alpha"), false, []));

        var results = await controller.DrainAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal(["beta", "gamma", "alpha"], runner.TasksRun);
    }

    [Fact]
    public async Task DrainAsync_EmptyQueue_ReturnsEmptyResultsAndCompletedState()
    {
        // An empty queue is a successful no-op: exit 0, no crash.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        var results = await controller.DrainAsync();

        Assert.Empty(results);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.Empty(runner.TasksRun);

        var exitCode = DrainOutcome.GetExitCode(results);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DrainAsync_EmptyQueue_TwoPhaseConstructor_ReturnsEmptyResults()
    {
        // Even with the two-phase constructor, an empty queue must
        // complete without touching Phase 1 or Phase 2.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var planRunner = new ScriptedSubagentRunner();
        var phase2Runner = new RecordingTaskRunner();

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => planRunner,
            planTestRunner: new ScriptedTestRunner());
        await controller.RefreshAsync();

        var results = await controller.DrainAsync();

        Assert.Empty(results);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.Empty(phase2Runner.TasksRun);
    }

    // ═══════════════════════════════════════════════════════════════
    // Controller integration: two-phase drain with subset
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DrainAsync_TwoPhase_WithTaskSubset_DrainsSubset()
    {
        // The two-phase constructor with a subset of tasks — both phases
        // must only operate on the subset tasks.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        var planRunner = new ScriptedSubagentRunner();
        planRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var phase2Runner = new RecordingTaskRunner();

        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: _ => planRunner,
            planTestRunner: new ScriptedTestRunner());
        await controller.RefreshAsync();

        // Select only alpha and gamma, in that order.
        controller.Tasks.Clear();
        controller.Tasks.Add(new RelayTaskItem(
            "alpha", Path.Combine(repo.Root, "llm-tasks", "alpha.md"),
            Path.Combine(repo.Root, ".relay", "alpha"), false, []));
        controller.Tasks.Add(new RelayTaskItem(
            "gamma", Path.Combine(repo.Root, "llm-tasks", "gamma.md"),
            Path.Combine(repo.Root, ".relay", "gamma"), false, []));

        var results = await controller.DrainAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Committed, r.Status));
        Assert.Equal(["alpha", "gamma"], phase2Runner.TasksRun);
        // beta must not have been touched.
        Assert.DoesNotContain("beta", phase2Runner.TasksRun);
    }

    [Fact]
    public async Task DrainAsync_TwoPhase_SingleTaskSubset_DrainsThatTask()
    {
        // Requesting a single task via the CLI: only that task drains.
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

        controller.Tasks.Clear();
        controller.Tasks.Add(new RelayTaskItem(
            "beta", Path.Combine(repo.Root, "llm-tasks", "beta.md"),
            Path.Combine(repo.Root, ".relay", "beta"), false, []));

        var results = await controller.DrainAsync();

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[0].Status);
        Assert.Equal("beta", results[0].TaskId);
        Assert.Equal(["beta"], phase2Runner.TasksRun);
    }
}
