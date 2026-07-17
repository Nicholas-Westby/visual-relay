using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the RestartBetweenTasks needs-review hold: flagged tasks are
/// excluded from the drain queue at build time to prevent unbounded
/// re-attempt loops. Standard and Sequential modes keep the 0dc9408
/// re-attempt behavior.
/// </summary>
public sealed class RelayQueueControllerRestartRbtHoldTests
{
    /// <summary>
    /// RBT drain with one pre-flagged task + one pending task: the flagged
    /// task is not started, its NEEDS-REVIEW marker and state dir are
    /// untouched, and the skip event appears in the drain log.
    /// Fails today: the flagged task runs when it reaches the queue head.
    /// </summary>
    [Fact]
    public async Task RbtDrain_PreFlaggedTask_NotStarted_MarkerUntouched()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Pre-flagged task with a NEEDS-REVIEW marker.
        repo.WriteTask("alpha", "# Flagged\n");
        repo.WriteNeedsReview("alpha", "test failure");

        // Pending task that should run normally.
        repo.WriteTask("beta", "# Pending\n");

        // Only beta has a scripted outcome — alpha must never be started.
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash-b", "sha-b", null));

        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        // Both tasks are visible in the controller.
        Assert.Contains(controller.Tasks, t => t.Id == "alpha" && t.NeedsReview);
        Assert.Contains(controller.Tasks, t => t.Id == "beta" && !t.NeedsReview);

        var results = await controller.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Only beta ran; alpha must NOT be in TasksRun.
        Assert.Single(results);
        Assert.Contains(results, r => r is { TaskId: "beta", Status: RelayTaskOutcomeStatus.Committed });
        Assert.DoesNotContain("alpha", runner.TasksRun);

        // Alpha's NEEDS-REVIEW marker must still exist (untouched).
        var alphaReviewPath = Path.Combine(repo.Root, ".relay", "alpha", "NEEDS-REVIEW");
        Assert.True(File.Exists(alphaReviewPath));

        // Alpha's state dir intact.
        Assert.True(Directory.Exists(Path.Combine(repo.Root, ".relay", "alpha")));

        // Drain log must contain skipped-needs-review event naming alpha.
        var drainLogs = Directory.GetFiles(Path.Combine(repo.Root, ".relay"), "drain-*.log");
        Assert.Single(drainLogs);
        var logContent = File.ReadAllText(drainLogs[0]);
        Assert.Contains("skipped-needs-review", logContent, StringComparison.Ordinal);
        Assert.Contains("alpha", logContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sequential mode: the flagged task IS re-attempted — regression-pin
    /// of commit 0dc9408's deliberate re-attempt behavior for
    /// Standard/Sequential.
    /// </summary>
    [Fact]
    public async Task SequentialDrain_PreFlaggedTask_IsReattempted()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Pre-flagged task.
        repo.WriteTask("alpha", "# Flagged\n");
        repo.WriteNeedsReview("alpha", "test failure");

        // Pending task.
        repo.WriteTask("beta", "# Pending\n");

        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Committed, "hash-a", "sha-a", null),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash-b", "sha-b", null));

        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        var results = await controller.DrainAsync(mode: RunAllMode.Sequential);

        // Both tasks ran in Sequential — alpha is re-attempted.
        Assert.Equal(2, results.Count);
        Assert.Contains("alpha", runner.TasksRun);
        Assert.Contains("beta", runner.TasksRun);
    }

    /// <summary>
    /// An always-flagging task in an RBT chain runs at most once (the cycle
    /// where it first flags, if it entered as pending) and never again in
    /// later cycles. Demonstrates the boundedness guarantee.
    /// </summary>
    [Fact]
    public async Task RbtChain_AlwaysFlaggingTask_RunsAtMostOnce()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // flaky: enters pending but always flags.
        repo.WriteTask("flaky", "# Always flags\n");
        // stable: commits cleanly.
        repo.WriteTask("stable", "# Stable\n");

        // ── Cycle 1: flaky flags, stable commits → handoff ──
        var runner1 = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("flaky", RelayTaskOutcomeStatus.Flagged, null, null, "always fails"),
            new RelayTaskOutcome("stable", RelayTaskOutcomeStatus.Committed, "hash-s", "sha-s", null));

        var controller1 = new RelayQueueController(repo.Root, runner1);
        await controller1.RefreshAsync();
        var results1 = await controller1.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Cycle 1: both tasks ran, flaky flagged, stable committed.
        Assert.Equal(2, results1.Count);
        Assert.Contains("flaky", runner1.TasksRun);
        Assert.Contains("stable", runner1.TasksRun);

        // Flaky now has a NEEDS-REVIEW marker.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "flaky", "NEEDS-REVIEW")));

        // ── Cycle 2: fresh controller, RefreshAsync, RBT drain ──
        // Flaky is now flagged; only stable should be available to run.
        // Script only stable's outcome — if flaky runs, the test fails on
        // missing outcome or wrong result count.
        var runner2 = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("stable", RelayTaskOutcomeStatus.Committed, "hash-s2", "sha-s2", null));

        var controller2 = new RelayQueueController(repo.Root, runner2);
        await controller2.RefreshAsync();

        // Flaky must appear in Tasks with NeedsReview=true.
        Assert.Contains(controller2.Tasks, t => t is { Id: "flaky", NeedsReview: true });
        Assert.Contains(controller2.Tasks, t => t is { Id: "stable", NeedsReview: false });

        var results2 = await controller2.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Only stable ran in cycle 2; flaky was filtered.
        Assert.Single(results2);
        Assert.Contains(results2, r => r.TaskId == "stable");
        Assert.DoesNotContain("flaky", runner2.TasksRun);

        // Flaky's NEEDS-REVIEW marker still exists.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "flaky", "NEEDS-REVIEW")));

        // Drain log for cycle 2 must contain the skip event.
        var drainLogs2 = Directory.GetFiles(Path.Combine(repo.Root, ".relay"), "drain-*.log");
        // At least one drain log exists (the two back-to-back DrainAsync
        // calls may share a second-resolution drainRunId, producing one
        // combined log — the content assertion is the reliable check).
        Assert.NotEmpty(drainLogs2);
        var logContents = drainLogs2.Select(File.ReadAllText).ToList();
        // At least one log (cycle 2) must contain skipped-needs-review.
        Assert.Contains(logContents, l => l.Contains("skipped-needs-review", StringComparison.Ordinal));
    }

    /// <summary>
    /// After a task-10 Reset of a flagged task (deleting the NEEDS-REVIEW
    /// marker and re-adding as Pending), the next RBT cycle runs it.
    /// The hold applies to flagged state, not the task id.
    /// </summary>
    [Fact]
    public async Task RbtDrain_AfterReset_FlaggedTaskRuns()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Pre-flagged task.
        repo.WriteTask("alpha", "# Flagged\n");
        repo.WriteNeedsReview("alpha", "test failure");

        // Pending task to keep RBT moving.
        repo.WriteTask("beta", "# Pending\n");

        // ── Cycle 1: RBT drain — alpha skipped (flagged), beta commits ──
        var runner1 = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash-b", "sha-b", null));

        var controller1 = new RelayQueueController(repo.Root, runner1);
        await controller1.RefreshAsync();
        var results1 = await controller1.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Alpha was not run.
        Assert.DoesNotContain("alpha", runner1.TasksRun);

        // ── Simulate task-10 Reset: delete NEEDS-REVIEW, re-add as Pending ──
        var reviewPath = Path.Combine(repo.Root, ".relay", "alpha", "NEEDS-REVIEW");
        Assert.True(File.Exists(reviewPath));
        File.Delete(reviewPath);
        Assert.False(File.Exists(reviewPath));

        // Now create a fresh controller, RefreshAsync — alpha should be Pending.
        // Alpha runs in RBT cycle 2 and commits → handoff stops drain.
        // (beta was already committed in cycle 1 but may still appear as
        // pending because the drain doesn't archive; in RBT alpha commits
        // first, stopping before beta reaches execution.)
        var runner2 = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Committed, "hash-a", "sha-a", null));

        var controller2 = new RelayQueueController(repo.Root, runner2);
        await controller2.RefreshAsync();

        // Alpha is now Pending (no NeedsReview).
        Assert.Contains(controller2.Tasks, t => t is { Id: "alpha", NeedsReview: false });

        var results2 = await controller2.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Alpha runs in cycle 2 (it was reset to pending).
        Assert.Contains("alpha", runner2.TasksRun);
    }
}
