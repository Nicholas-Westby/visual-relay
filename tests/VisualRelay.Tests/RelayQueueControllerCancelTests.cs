using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Cancelling a drain must leave the queue somewhere an operator can restart from:
/// the interrupted task reset and marked for review, the tasks behind it untouched
/// and still pending, and no halt marker — a cancel is not a fault the circuit
/// breaker should count.
/// </summary>
public sealed class RelayQueueControllerCancelTests
{
    [Fact]
    public async Task DrainAsync_CancelledMidTask_ResetsThatTaskAndLeavesTheRestPending()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("interrupted", "# Interrupted\n");
        repo.WriteTask("queued", "# Queued\n");
        // The reset needs a baseline snapshot to act on; without one it refuses.
        var taskDirectory = Path.Combine(repo.Root, ".relay", "interrupted");
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "pre-run-untracked.txt"), "", CancellationToken.None);
        var git = new RecordingGitInvoker();

        using var cts = new CancellationTokenSource();
        var runner = new CancelAwaitingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner, gitInvoker: git);
        await controller.RefreshAsync(CancellationToken.None);
        var drain = controller.DrainAsync(cts.Token);
        await runner.FirstStarted;
        await cts.CancelAsync();
        var results = await drain;

        // The interrupted task is the only one the drain touched.
        Assert.Equal("interrupted", Assert.Single(results).TaskId);
        Assert.Equal(["interrupted"], runner.TasksRun);

        // Its tree is restored and it is marked for review with the cancel reason.
        Assert.Contains(git.Calls, c => c is ["checkout", "--", "."]);
        var marker = await File.ReadAllTextAsync(Path.Combine(taskDirectory, "NEEDS-REVIEW"), CancellationToken.None);
        Assert.Contains("cancelled by operator", marker, StringComparison.Ordinal);

        // The queued task is untouched: still pending, no marker of its own.
        Assert.Contains(controller.Tasks, t => t is { Id: "queued", NeedsReview: false });
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "queued", "NEEDS-REVIEW")));

        // A cancel is an operator decision, not a run of failures.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
        Assert.Equal(RelayQueueState.Cancelled, controller.State);
    }

    [Fact]
    public async Task DrainAsync_CancelledDuringPlanning_LeavesEveryTaskPendingWithNoMarker()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        var sim = PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        using var cts = new CancellationTokenSource();
        var planRunner = new CancelAwaitingSubagentRunner();
        var phase2Runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(
            repo.Root, phase2Runner,
            planSubagentRunnerFactory: (_, _) => planRunner,
            planTestRunner: new ScriptedTestRunner(),
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim);

        await controller.RefreshAsync(CancellationToken.None);
        var drain = controller.DrainAsync(cts.Token);
        await planRunner.FirstStarted;
        await cts.CancelAsync();
        var results = await drain;

        Assert.Empty(results);
        Assert.Empty(phase2Runner.TasksRun);
        Assert.Equal(RelayQueueState.Cancelled, controller.State);
        Assert.All(controller.Tasks, t => Assert.False(t.NeedsReview));
        foreach (var id in new[] { "alpha", "beta" })
            Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", id, "NEEDS-REVIEW")));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }

    [Fact]
    public async Task DrainAsync_CancelDuringACommitInRestartMode_StopsInsteadOfHandingOffARestart()
    {
        // Stage 12 finishes on a fresh token, so a task cancelled mid-commit still
        // commits. In restart mode that commit used to hand off a relaunch, and the
        // next instance resumed the drain — the operator's cancel simply lost.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("committing", "# Committing\n");
        repo.WriteTask("queued", "# Queued\n");

        using var cts = new CancellationTokenSource();
        var runner = new CommitThenCancelTaskRunner(cts);
        var controller = new RelayQueueController(repo.Root, runner, gitInvoker: new RecordingGitInvoker());
        RestartHandoff? handoff = null;
        controller.OnRestartRequested = h => handoff = h;
        await controller.RefreshAsync(CancellationToken.None);

        var results = await controller.DrainAsync(cts.Token, RunAllMode.RestartBetweenTasks);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, Assert.Single(results).Status);
        Assert.Equal(["committing"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Cancelled, controller.State);
        Assert.Null(handoff);
        Assert.Null(RestartHandoff.Read(repo.Root));
    }

    /// <summary>Commits the task, having cancelled the drain while the commit was sealing.</summary>
    private sealed class CommitThenCancelTaskRunner(CancellationTokenSource cts) : IRelayTaskRunner
    {
        public List<string> TasksRun { get; } = [];

        public Task<RelayTaskOutcome> RunTaskAsync(
            string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            TasksRun.Add(taskId);
            cts.Cancel();
            return Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "sha", null));
        }
    }

    /// <summary>Stays in-flight until the drain is cancelled, then throws like a torn stage.</summary>
    private sealed class CancelAwaitingTaskRunner : IRelayTaskRunner
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstStarted => _firstStarted.Task;
        public List<string> TasksRun { get; } = [];

        public async Task<RelayTaskOutcome> RunTaskAsync(
            string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            TasksRun.Add(taskId);
            _firstStarted.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "sha", null);
        }
    }

    /// <summary>A planning stage that stays in-flight until the drain is cancelled.</summary>
    private sealed class CancelAwaitingSubagentRunner : ISubagentRunner
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstStarted => _firstStarted.Task;

        public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            _firstStarted.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("unreachable: the planning stage is only ever cancelled");
        }
    }

    /// <summary>Records the argument list of every git command the drain runs.</summary>
    private sealed class RecordingGitInvoker : IGitInvoker
    {
        public List<string[]> Calls { get; } = [];

        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult((0, string.Empty, false));
        }
    }
}
