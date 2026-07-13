using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayQueueControllerWorktreeResetTests
{
    /// <summary>
    /// A task runner that on its first invocation dirties the worktree
    /// (tracked modification + authored untracked file) and returns Flagged,
    /// and on its second invocation asserts the tree is clean before
    /// returning Committed.
    /// </summary>
    private sealed class DirtyThenCleanAssertingRunner : IRelayTaskRunner
    {
        private int _callCount;
        public List<string> TasksRun { get; } = [];

        public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            _callCount++;
            TasksRun.Add(taskId);

            if (_callCount == 1)
            {
                // Dirty the worktree: modify a tracked file and create an
                // authored untracked file — exactly the contamination scenario.
                var trackedPath = Path.Combine(rootPath, "src", "app.cs");
                File.WriteAllText(trackedPath, "contaminated-by-" + taskId);

                var untrackedPath = Path.Combine(rootPath, "tests", "hanging.test");
                Directory.CreateDirectory(Path.GetDirectoryName(untrackedPath)!);
                File.WriteAllText(untrackedPath, "while(true){}");

                return Task.FromResult(new RelayTaskOutcome(
                    taskId, RelayTaskOutcomeStatus.Flagged, null, null, "verify timeout"));
            }

            // Second call (and any subsequent): assert the tree is clean.
            // Tracked file must be back to HEAD content.
            var actualTracked = File.ReadAllText(Path.Combine(rootPath, "src", "app.cs"));
            Assert.Equal("original", actualTracked);

            // Authored untracked file must not exist.
            var hangingTest = Path.Combine(rootPath, "tests", "hanging.test");
            Assert.False(File.Exists(hangingTest),
                $"hanging test file from flagged task must not exist for task '{taskId}'");

            return Task.FromResult(new RelayTaskOutcome(
                taskId, RelayTaskOutcomeStatus.Committed, "hash-ab", "sha-cd", null));
        }
    }

    // The drain's flagged-task reset path (RelayQueueController.ResetAndLogAsync →
    // WorktreeResetter.ResetAsync) now takes an injected IGitInvoker, so a repo-bound
    // GitSim backs the reset: `reset -q HEAD` + `checkout -- .` restore the tracked
    // file to HEAD and `ls-files --others` enumerates the real-disk untracked file the
    // resetter then deletes — exactly the real-git behavior, now always-on.
    [Fact]
    public async Task DrainAsync_FlaggedTask_ResetsWorktreeBeforeNextTask()
    {
        // 1. Create a GitSim repo with a tracked file plus two queued tasks.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — will flag with dirty tree\n");
        repo.WriteTask("beta", "# Beta — must inherit clean tree\n");

        // Register a repo and commit a tracked file.
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "original");
        sim.Commit(repo.Root, "seed");

        // Write a pre-run untracked snapshot for both tasks (empty — no
        // pre-existing untracked files).  This is what the real driver does
        // before stage 1.
        foreach (var tid in new[] { "alpha", "beta" })
        {
            var snapshotDir = Path.Combine(repo.Root, ".relay", tid);
            Directory.CreateDirectory(snapshotDir);
            File.WriteAllText(Path.Combine(snapshotDir, "pre-run-untracked.txt"), "");
        }

        var runner = new DirtyThenCleanAssertingRunner();
        var controller = new RelayQueueController(repo.Root, runner, gitInvoker: sim);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Both tasks must have been attempted.
        Assert.Equal(["alpha", "beta"], runner.TasksRun);
        Assert.Equal(2, results.Count);

        // Alpha is flagged.
        Assert.Equal("alpha", results[0].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, results[0].Status);

        // Beta ran normally.
        Assert.Equal("beta", results[1].TaskId);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[1].Status);

        // Alpha is set aside for review.
        var flaggedTask = controller.Tasks.SingleOrDefault(t => t.Id == "alpha");
        Assert.NotNull(flaggedTask);
        Assert.True(flaggedTask!.NeedsReview);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "alpha", "NEEDS-REVIEW")));

        // Beta is removed (committed).
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "beta");

        // No halt marker — the drain finished.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));

        // State reflects that at least one task needs review.
        Assert.Equal(RelayQueueState.ReviewNeeded, controller.State);
    }

    [Fact]
    public async Task DrainAsync_FlaggedTask_LogsResetRemoved()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha — will flag with dirty tree\n");
        repo.WriteTask("beta", "# Beta — must inherit clean tree\n");

        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "original");
        sim.Commit(repo.Root, "seed");

        foreach (var tid in new[] { "alpha", "beta" })
        {
            var snapshotDir = Path.Combine(repo.Root, ".relay", tid);
            Directory.CreateDirectory(snapshotDir);
            File.WriteAllText(Path.Combine(snapshotDir, "pre-run-untracked.txt"), "");
        }

        var runner = new DirtyThenCleanAssertingRunner();
        var controller = new RelayQueueController(repo.Root, runner, gitInvoker: sim);

        await controller.RefreshAsync();
        await controller.DrainAsync();

        // The drain log must contain a reset-removed line for alpha.
        var drainLogs = Directory.GetFiles(Path.Combine(repo.Root, ".relay"), "drain-*.log");
        Assert.Single(drainLogs);
        var log = await File.ReadAllTextAsync(drainLogs[0]);
        Assert.Contains("reset-removed", log, StringComparison.Ordinal);
        Assert.Contains("1 untracked file(s)", log, StringComparison.Ordinal);
    }
}
