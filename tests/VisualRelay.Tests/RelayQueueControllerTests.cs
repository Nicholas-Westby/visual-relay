using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerTests
{
    [Fact]
    public async Task DrainAsync_UsesManualOrderAndPausesAtTaskBoundary()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        runner.AfterRun = controller.RequestPause;

        await controller.RefreshAsync();
        controller.MoveDown("alpha");
        var results = await controller.DrainAsync();

        Assert.Equal(["beta"], runner.TasksRun);
        Assert.Equal(["beta"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Paused, controller.State);
        Assert.Equal(["alpha", "gamma"], controller.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task DrainAsync_HaltsAfterRepeatedCommitGateRejections()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var controller = new RelayQueueController(repo.Root, new CommitRejectingTaskRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Equal(["alpha", "beta"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Failed, controller.State);
        // Alpha and beta were both flagged and set aside for review; gamma is un-run.
        Assert.Contains(controller.Tasks, t => t is { Id: "alpha", NeedsReview: true });
        Assert.Contains(controller.Tasks, t => t is { Id: "beta", NeedsReview: true });
        Assert.Contains(controller.Tasks, t => t is { Id: "gamma", NeedsReview: false });
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    [Fact]
    public async Task DrainAsync_ContinuesPastIsolatedFlag()
    {
        // 3 tasks, the middle one Flagged — all 3 must run; the flagged one is
        // set aside as NeedsReview; the drain does NOT stop after the flag.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null, "author-tests did not go red"),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            new RelayTaskOutcome("gamma", RelayTaskOutcomeStatus.Committed, "hash", "sha", null));
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // All 3 tasks ran.
        Assert.Equal(["alpha", "beta", "gamma"], runner.TasksRun);
        Assert.Equal(["alpha", "beta", "gamma"], results.Select(r => r.TaskId));

        // The flagged task is set aside for review.
        var flaggedTask = controller.Tasks.SingleOrDefault(t => t.Id == "alpha");
        Assert.NotNull(flaggedTask);
        Assert.True(flaggedTask!.NeedsReview);
        Assert.Contains("author-tests did not go red", flaggedTask.ReviewReason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "alpha", "NEEDS-REVIEW")));

        // Completed tasks are removed; the flagged task was re-added.
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "beta");
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "gamma");

        // State reflects that at least one task needs review.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);

        // No halt marker — the drain finished (with flags), it didn't halt early.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    [Fact]
    public async Task DrainAsync_ContinuesPastStalledTaskWithTimeoutReason()
    {
        // A stalled task (Flagged with a timeout reason) is treated like any
        // other flag — skip + continue, not an immediate halt.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null, "swival timed out after 300s"),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash", "sha", null));
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Both tasks ran — the timeout flag did not halt the drain.
        Assert.Equal(["alpha", "beta"], runner.TasksRun);
        Assert.Equal(["alpha", "beta"], results.Select(r => r.TaskId));

        // The stalled task is set aside for review.
        var stalledTask = controller.Tasks.SingleOrDefault(t => t.Id == "alpha");
        Assert.NotNull(stalledTask);
        Assert.True(stalledTask!.NeedsReview);
        Assert.Contains("swival timed out", stalledTask.ReviewReason, StringComparison.Ordinal);

        // No halt marker.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    [Fact]
    public async Task DrainAsync_HaltsAtConsecutiveFlagThreshold()
    {
        // ConsecutiveFlagThreshold (3) consecutive flags → drain halts, writes
        // DRAIN-HALTED, leaves the rest un-run.
        // Tasks sort alphabetically: alpha, beta, delta, gamma.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        repo.WriteTask("delta", "# Delta\n");
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null, "flag one"),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Flagged, null, null, "flag two"),
            new RelayTaskOutcome("gamma", RelayTaskOutcomeStatus.Flagged, null, null, "flag three"),
            new RelayTaskOutcome("delta", RelayTaskOutcomeStatus.Flagged, null, null, "flag four"));
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Alphabetical order: alpha, beta, delta run; gamma is the 4th and
        // never started because the threshold was reached on delta (3rd flag).
        Assert.Equal(["alpha", "beta", "delta"], runner.TasksRun);
        Assert.Equal(["alpha", "beta", "delta"], results.Select(r => r.TaskId));

        // Gamma remains un-run.
        Assert.Contains(controller.Tasks, t => t is { Id: "gamma", NeedsReview: false });

        // The three flagged tasks are set aside for review.
        Assert.Contains(controller.Tasks, t => t is { Id: "alpha", NeedsReview: true });
        Assert.Contains(controller.Tasks, t => t is { Id: "beta", NeedsReview: true });
        Assert.Contains(controller.Tasks, t => t is { Id: "delta", NeedsReview: true });

        // DRAIN-HALTED marker was written.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));

        // State reflects the halt.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);
    }

    [Fact]
    public async Task DrainAsync_CommittedBetweenFlagsResetsCounter()
    {
        // flag, commit, flag, commit, flag — the Committed outcomes reset the
        // consecutive-flag counter, so the threshold is never reached and the
        // drain finishes all tasks.
        // Tasks sort alphabetically: alpha, beta, delta, epsilon, gamma.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        repo.WriteTask("delta", "# Delta\n");
        repo.WriteTask("epsilon", "# Epsilon\n");
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null, "flag one"),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            new RelayTaskOutcome("gamma", RelayTaskOutcomeStatus.Flagged, null, null, "flag two"),
            new RelayTaskOutcome("delta", RelayTaskOutcomeStatus.Committed, "hash", "sha", null),
            new RelayTaskOutcome("epsilon", RelayTaskOutcomeStatus.Flagged, null, null, "flag three"));
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // All 5 tasks ran — Committed outcomes reset the counter so the
        // consecutive-flag threshold was never reached.
        // Alphabetical order: alpha, beta, delta, epsilon, gamma.
        Assert.Equal(["alpha", "beta", "delta", "epsilon", "gamma"], runner.TasksRun);
        Assert.Equal(["alpha", "beta", "delta", "epsilon", "gamma"], results.Select(r => r.TaskId));

        // No halt marker.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));

        // State reflects that flagged tasks need review.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);

        // The flagged tasks are set aside for review (alpha, delta, gamma in
        // alphabetical run order received the three Flagged outcomes).
        Assert.Contains(controller.Tasks, t => t is { Id: "alpha", NeedsReview: true });
        Assert.Contains(controller.Tasks, t => t is { Id: "delta", NeedsReview: true });
        Assert.Contains(controller.Tasks, t => t is { Id: "gamma", NeedsReview: true });

        // Committed tasks are removed.
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "beta");
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "epsilon");
    }

    [Fact]
    public async Task DrainAsync_ClearsStaleHaltMarkerWhenStarting()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED"), "stale\n");
        var controller = new RelayQueueController(repo.Root, new RecordingTaskRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Equal(["alpha"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    [Fact]
    public async Task DrainAsync_TaskAddedMidRun_IsNotRunByCurrentDrainButRemainsForNext()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — the callback fires only
        // during the awaited DrainAsync below, while 'repo' (using var) is still alive;
        // disposal happens at method exit, after the drain completes.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                controller.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync();

        // Delta was added mid-drain and must NOT have been entered.
        Assert.Equal(["alpha", "beta", "gamma"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        // Delta remains available for the next drain.
        Assert.Contains(controller.Tasks, t => t.Id == "delta");
    }

    [Fact]
    public async Task DrainAsync_InterleavedRefreshDoesNotChangeOrderOrDuplicate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires only during the
        // awaited DrainAsync below; 'repo' (using var) stays alive until method exit.
        runner.AfterRunAsync = async () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                // Simulate a new task file landing on disk mid-run, then refresh.
                // The refresh re-sorts Tasks so aardvark shifts to index 0,
                // displacing beta from the head position.
                repo.WriteTask("aardvark", "# Aardvark\n");
                await controller.RefreshAsync();
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync();

        // Snapshot preserves original order; aardvark must NOT displace the
        // in-flight drain and the original three tasks must all complete.
        Assert.Equal(["alpha", "beta", "gamma"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        // Aardvark remains for the next drain.
        Assert.Contains(controller.Tasks, t => t.Id == "aardvark");
    }

}
