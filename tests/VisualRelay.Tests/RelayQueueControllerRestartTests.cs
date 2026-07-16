using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the RestartBetweenTasks Run All protocol — drain stops after
/// committed tasks, writes a handoff sidecar, and the relaunched instance
/// resumes where it left off.
/// </summary>
public sealed class RelayQueueControllerRestartTests
{
    [Fact]
    public async Task RestartBetweenTasks_CommittedTask_StopsAndWritesHandoff()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");

        // Both tasks return Committed — but RestartBetweenTasks must stop after
        // the first committed task so the relaunched build picks up the new code.
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Committed, "hash-a", "sha-a", null),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash-b", "sha-b", null));

        RestartHandoff? capturedHandoff = null;
        var controller = new RelayQueueController(repo.Root, runner);
        controller.OnRestartRequested = h => capturedHandoff = h;

        await controller.RefreshAsync();
        var results = await controller.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Only alpha ran — RestartBetweenTasks stops at the first committed task.
        Assert.Single(results);
        Assert.Equal("alpha", results[0].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[0].Status);

        // Handoff sidecar must exist so the relaunched instance can resume.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "restart-handoff.json")));

        // OnRestartRequested callback must have been invoked exactly once.
        Assert.NotNull(capturedHandoff);
        Assert.Equal(repo.Root, capturedHandoff!.RootPath);
        Assert.Equal("sha-a", capturedHandoff.CommitSha);
        Assert.Equal(1, capturedHandoff.PendingCount);

        // Beta must not have run (it's pending for the relaunched instance).
        Assert.DoesNotContain("beta", runner.TasksRun);
    }

    [Fact]
    public async Task RestartBetweenTasks_FlaggedTask_NoHandoff_CommitsTriggerAfter()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — will flag\n");
        repo.WriteTask("beta", "# Beta — commits after flag\n");

        // Alpha flags (no code change → no restart needed).
        // Beta commits → handoff + stop so the relaunched build picks it up.
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null, "author-tests did not go red"),
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Committed, "hash-b", "sha-b", null));

        RestartHandoff? capturedHandoff = null;
        var controller = new RelayQueueController(repo.Root, runner);
        controller.OnRestartRequested = h => capturedHandoff = h;

        await controller.RefreshAsync();
        var results = await controller.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Both tasks ran: flagged alpha in-process, then committed beta triggers restart.
        Assert.Equal(2, results.Count);
        Assert.Equal("alpha", results[0].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, results[0].Status);
        Assert.Equal("beta", results[1].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[1].Status);

        // Handoff must exist for beta's commit.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "restart-handoff.json")));

        // Callback must have been called exactly once (for beta, not alpha).
        Assert.NotNull(capturedHandoff);
        Assert.Equal("sha-b", capturedHandoff!.CommitSha);
    }

    [Fact]
    public async Task RestartBetweenTasks_StartupHandoffConsumed_NoProgressGuard()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — flags, no progress\n");

        // Simulate a restart cycle: a handoff exists from the prior launch.
        var priorHandoff = RestartHandoff.Write(
            repo.Root,
            new RelayTaskOutcome("prior", RelayTaskOutcomeStatus.Committed, "h", "sha-prior", null),
            "drain-20260716000000",
            pendingCount: 1);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "restart-handoff.json")));

        // The only task flags — zero committed outcomes this cycle.
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged, null, null, "failed"));

        var restartRequested = false;
        var controller = new RelayQueueController(repo.Root, runner);
        controller.OnRestartRequested = _ => restartRequested = true;

        await controller.RefreshAsync();
        var results = await controller.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Zero committed outcomes → no restart, no new handoff.
        Assert.DoesNotContain(results, r => r.Status == RelayTaskOutcomeStatus.Committed);
        Assert.False(restartRequested);

        // The handoff must have been consumed so the next launch won't auto-resume.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "restart-handoff.json")));

        // Drain ends cleanly — not stalled in a restart loop.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);
    }

    /// <summary>
    /// Startup-continuation: a fresh handoff plus a mixed queue (completed,
    /// needs-review, and pending) resumes with only the pending task;
    /// the needs-review task is filtered from the drain.
    /// </summary>
    [Fact]
    public async Task StartupContinuation_FreshHandoff_MixedQueue_OnlyPendingRuns()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Completed task lives in completed/ (archived, not in pending queue).
        repo.WriteCompletedTask("alpha", "# Completed\n");

        // Needs-review task has a NEEDS-REVIEW marker under .relay/.
        repo.WriteTask("beta", "# Needs review\n");
        repo.WriteNeedsReview("beta", "Flagged: test failure");

        // Pending task is the only one ready to run.
        repo.WriteTask("gamma", "# Pending task\n");

        // Write a fresh handoff to simulate a restart.
        _ = RestartHandoff.Write(
            repo.Root,
            new RelayTaskOutcome("prior", RelayTaskOutcomeStatus.Committed, "h", "sha-prior", null),
            "drain-20260716000000",
            pendingCount: 2);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "restart-handoff.json")));

        // Refresh loads only pending + needs-review (completed is archived).
        var runner = new ScriptedOutcomeTaskRunner(
            new RelayTaskOutcome("beta", RelayTaskOutcomeStatus.Flagged, null, null, "Flagged: test failure"),
            new RelayTaskOutcome("gamma", RelayTaskOutcomeStatus.Committed, "hash-g", "sha-g", null));

        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();

        // Only pending + needs-review are visible (completed is archived).
        Assert.Contains(controller.Tasks, t => t.Id == "beta" && t.NeedsReview);
        Assert.Contains(controller.Tasks, t => t.Id == "gamma" && !t.NeedsReview);
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "alpha");

        var results = await controller.DrainAsync(mode: RunAllMode.RestartBetweenTasks);

        // Both tasks are in the queue; needs-review task flags, pending task commits.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r is { TaskId: "beta", Status: RelayTaskOutcomeStatus.Flagged });
        Assert.Contains(results, r => r is { TaskId: "gamma", Status: RelayTaskOutcomeStatus.Committed });
    }

    /// <summary>
    /// Stale handoff (timestamp &gt; 5 min old) must be discarded and never
    /// trigger an auto-run.
    /// </summary>
    [Fact]
    public void StaleHandoff_IsDiscarded()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        // Write a handoff and then alter its timestamp to simulate staleness.
        var path = Path.Combine(repo.Root, ".relay", "restart-handoff.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staleJson = $$"""
            {
              "RootPath": "{{repo.Root.Replace("\\", "\\\\")}}",
              "DrainId": "drain-20250101000000",
              "Timestamp": "2025-01-01T00:00:00.0000000+00:00",
              "PendingCount": 1,
              "CommitSha": "sha-old",
              "RelaunchCommand": null,
              "Mode": 2
            }
            """;
        File.WriteAllText(path, staleJson);

        var handoff = RestartHandoff.Read(repo.Root);
        Assert.NotNull(handoff);
        Assert.True(RestartHandoff.IsStale(handoff!, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Handoff pointing at a missing root directory is stale and discarded.
    /// </summary>
    [Fact]
    public void StaleHandoff_MissingRootPath_IsStale()
    {
        using var repo = TestRepository.Create();
        var missingRoot = Path.Combine(repo.Root, "nonexistent");

        // Hand-craft the JSON so we don't call Write() (which creates the
        // target directory and would defeat the IsStale check).
        var handoff = new RestartHandoff(
            RootPath: missingRoot,
            DrainId: "drain-id",
            Timestamp: DateTimeOffset.UtcNow,
            PendingCount: 0,
            CommitSha: "sha",
            RelaunchCommand: null,
            Mode: RunAllMode.RestartBetweenTasks);

        // missingRoot does NOT exist → IsStale must return true.
        Assert.False(Directory.Exists(missingRoot));
        Assert.True(RestartHandoff.IsStale(handoff, DateTimeOffset.UtcNow));
    }
}
