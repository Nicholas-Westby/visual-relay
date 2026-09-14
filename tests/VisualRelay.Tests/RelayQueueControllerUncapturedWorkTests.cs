using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Measured on Windows 11: a task flagged at the commit, its capture failed without a
/// word, the drain moved on, and the next task ran over the flagged task's verified
/// work. A flag whose work was not captured now stops the drain with the tree as the
/// flag left it.
/// </summary>
public sealed class RelayQueueControllerUncapturedWorkTests
{
    [Fact]
    public async Task DrainAsync_AFlagWhoseWorkWasNotCaptured_StopsBeforeTheNextTaskStarts()
    {
        using var repo = TestRepository.Create();
        var (controller, runner) = await StartDrainOverAFlaggedTreeAsync(repo);

        var results = await controller.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["alpha"], runner.TasksRun);
        Assert.Equal("alpha", Assert.Single(results).TaskId);
        Assert.Contains(controller.Tasks, t => t is { Id: "beta", NeedsReview: false });
        Assert.Contains("could not be saved", DrainCircuitBreaker.ReadHaltReason(repo.Root), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DrainAsync_AFlagWhoseWorkWasNotCaptured_LeavesTheTreeUnreset()
    {
        using var repo = TestRepository.Create();
        var (controller, _) = await StartDrainOverAFlaggedTreeAsync(repo);

        await controller.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal("verified work", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(repo.Root, "src", "added.cs")), "the flag's new file must survive");
        Assert.Contains("reset-skipped-work-uncaptured", await ReadDrainLogAsync(repo), StringComparison.Ordinal);
    }

    /// <summary>
    /// The stage-12 reset skip used to log "commit sealed" even when git had rejected the
    /// commit and nothing landed. Its log line has to be true either way.
    /// </summary>
    [Fact]
    public async Task DrainAsync_AStage12Flag_LogsTheResetSkipWithoutClaimingTheCommitLanded()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var taskDirectory = Path.Combine(repo.Root, ".relay", "alpha");
        Directory.CreateDirectory(taskDirectory);
        await StageStatusRecord.WriteAsync(taskDirectory,
            Enumerable.Range(1, 12).Select(n => new StageStatusEntry(n, $"Stage {n}", n == 12 ? "Flagged" : "Done")).ToList(),
            TestContext.Current.CancellationToken);
        var runner = new ScriptedOutcomeTaskRunner(new RelayTaskOutcome("alpha", RelayTaskOutcomeStatus.Flagged,
            null, null, "commit rejected: (git exit 128): Author identity unknown"));
        var controller = new RelayQueueController(repo.Root, runner, gitInvoker: RelayDriverTestHelpers.InitSim(repo));
        await controller.RefreshAsync(TestContext.Current.CancellationToken);

        await controller.DrainAsync(TestContext.Current.CancellationToken);

        var log = await ReadDrainLogAsync(repo);
        Assert.Contains("stage 12 flagged; skipping worktree reset to preserve flag evidence", log, StringComparison.Ordinal);
        Assert.DoesNotContain("commit sealed", log, StringComparison.Ordinal);
    }

    /// <summary>Two queued tasks over a committed <c>src/app.cs</c>; the first flags with its work uncaptured.</summary>
    private static async Task<(RelayQueueController Controller, UncapturedFlagRunner Runner)> StartDrainOverAFlaggedTreeAsync(
        TestRepository repo)
    {
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "original");
        sim.Commit(repo.Root, "seed");
        // The baseline a reset would act on, as the driver records before stage 1.
        foreach (var id in new[] { "alpha", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", id));
            await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", id, "pre-run-untracked.txt"), "",
                TestContext.Current.CancellationToken);
        }

        var runner = new UncapturedFlagRunner();
        var controller = new RelayQueueController(repo.Root, runner, gitInvoker: sim);
        await controller.RefreshAsync(TestContext.Current.CancellationToken);
        return (controller, runner);
    }

    private static Task<string> ReadDrainLogAsync(TestRepository repo) =>
        File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(repo.Root, ".relay"), "drain-*.log")),
            TestContext.Current.CancellationToken);

    /// <summary>Leaves verified work in the tree and flags with it uncaptured, as a refused capture does.</summary>
    private sealed class UncapturedFlagRunner : IRelayTaskRunner
    {
        public List<string> TasksRun { get; } = [];

        public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            TasksRun.Add(taskId);
            File.WriteAllText(Path.Combine(rootPath, "src", "app.cs"), "verified work");
            File.WriteAllText(Path.Combine(rootPath, "src", "added.cs"), "new file");
            return Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null,
                "commit rejected: (git exit 128): Author identity unknown") { WorkUncaptured = true });
        }
    }
}
