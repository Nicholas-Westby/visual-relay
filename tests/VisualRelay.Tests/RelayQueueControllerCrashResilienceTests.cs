using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerCrashResilienceTests
{
    [Fact]
    public async Task DrainAsync_UnhandledExceptionFromRunTask_ContinuesToNextTask()
    {
        // A runner whose first call throws InvalidOperationException (simulating
        // a secondary exception from FlagAsync, e.g. EMFILE on Directory.CreateDirectory).
        // The drain must catch it, convert to a Flagged outcome, and continue.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        var runner = new ThrowingThenCommittedTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Both tasks must have been attempted — the crash on alpha does not abort.
        Assert.Equal(["alpha", "beta"], runner.TasksRun);
        Assert.Equal(2, results.Count);

        // Alpha is flagged with the exception reason.
        Assert.Equal("alpha", results[0].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, results[0].Status);
        Assert.Contains("InvalidOperationException", results[0].Reason, StringComparison.Ordinal);

        // Beta ran normally.
        Assert.Equal("beta", results[1].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[1].Status);

        // State reflects that at least one task needs review.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);
    }

    [Fact]
    public async Task DrainAsync_ConsecutiveExceptions_HaltsAfterThreshold()
    {
        // A runner that throws on every task.  Three consecutive unhandled-
        // exception flags must trigger the circuit breaker and halt the drain.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        repo.WriteTask("delta", "# Delta\n");
        var runner = new AlwaysThrowingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Only 3 tasks ran — the circuit breaker halted on the third consecutive flag.
        Assert.Equal(3, runner.TasksRun.Count);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(RelayTaskOutcomeStatus.Flagged, r.Status));

        // Gamma is un-run (remains as a pending task, not NeedsReview).
        // Tasks sort alphabetically: alpha, beta, delta, gamma — delta is 3rd
        // and triggers the circuit breaker; gamma is the 4th, never started.
        Assert.Contains(controller.Tasks, t => t.Id == "gamma" && !t.NeedsReview);

        // DRAIN-HALTED marker was written.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));

        // State reflects the halt.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);
    }

    [Fact]
    public async Task FlagAsync_DirectoryCreateFails_ReturnsFlag()
    {
        // Simulate the EMFILE bug: .relay/taskId exists as a file (not a
        // directory), so Directory.CreateDirectory throws both at line 32 of
        // RunTaskAsync and again inside FlagAsync.  The secondary throw must
        // not escape — RunTaskAsync must return a Flagged outcome.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("crashy", "# Crashy\n");

        // Create .relay/crashy as a FILE so Directory.CreateDirectory throws.
        var taskDir = Path.Combine(repo.Root, ".relay", "crashy");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(taskDir, "trap");

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new ScriptedSubagentRunner(),
                new ScriptedTestRunner(),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "crashy");

        // Must return a valid Flagged outcome instead of throwing.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Equal("crashy", outcome.TaskId);
        Assert.NotNull(outcome.Reason);
    }
}
