using VisualRelay.Core.Configuration;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Runs the planning stages (1–4) for multiple tasks in parallel, each in its
/// own ephemeral git worktree for full isolation. After planning completes
/// (successfully or flagged), artifacts are copied back to the main repo.
/// Returns a result per task in input order.
/// </summary>
public static class PlanPhaseRunner
{
    /// <summary>
    /// Runs planning (stages 1–4) for each task concurrently, bounded by
    /// <paramref name="config"/>.MaxPlanConcurrency. Artifacts are copied back
    /// to <paramref name="mainRootPath"/>.
    /// </summary>
    /// <param name="mainRootPath">Main repo root that artifacts are copied back to.</param>
    /// <param name="tasks">
    /// Task ids paired with a factory that builds the subagent runner for each. A factory
    /// rather than a runner because the sink a planning task publishes to only exists once
    /// its worktree does: it composes the caller's observable sink with the file sink that
    /// writes the task's run.log. Building the agent from that sink is what puts the
    /// planning stages' tool calls and thinking in the same log as every other stage.
    /// </param>
    /// <param name="config">Drive configuration; <c>MaxPlanConcurrency</c> bounds parallelism.</param>
    /// <param name="testRunner">Test runner used by the planning stages.</param>
    /// <param name="cancellationToken">Cancels all in-flight planning work.</param>
    /// <param name="eventSinkFactory">
    /// Optional factory that creates an observable <see cref="IRelayEventSink"/>
    /// per task so live progress reaches the GUI. When null, a
    /// <see cref="NullRelayEventSink"/> is used (no live progress).
    /// </param>
    /// <param name="environmentAccessor">
    /// Optional environment accessor threaded into the <see cref="RelayDriverDependencies"/>
    /// each planning driver is built from, so the once-per-run vr-guard profile self-heal
    /// (<see cref="NonoProfileEnsurer.EnsureAsync"/>) resolves its path through it. Production
    /// leaves it <c>null</c> (real process env, real <c>~/.config</c>); tests inject a hermetic
    /// temp-XDG accessor so planning never writes the user's real profile.
    /// </param>
    /// <param name="gitInvoker">
    /// Optional git process factory threaded into every worktree operation
    /// (<see cref="PlanningWorktree.CreateAsync"/>/<see cref="PlanningWorktree.RemoveAsync"/>/
    /// <see cref="PlanningWorktree.PruneLeftoversAsync"/>) and into the
    /// <see cref="RelayDriverDependencies"/> each planning driver is built from.
    /// Production leaves it <c>null</c> (real <see cref="GitInvoker"/>); tests inject a
    /// repo-bound in-memory sim so the whole plan phase runs without the real git binary.
    /// </param>
    /// <param name="sandboxHost">
    /// Optional host threaded beside <paramref name="environmentAccessor"/>, deciding where
    /// that self-heal places the profile. Production leaves it <c>null</c> (this machine, a
    /// WSL distro on Windows); tests state the local host so planning never writes a distro's.
    /// </param>
    public static async Task<List<(string TaskId, RelayTaskOutcome Outcome)>> RunPlanPhaseAsync(
        string mainRootPath,
        IEnumerable<(string TaskId, Func<IRelayEventSink, ISubagentRunner> RunnerFactory)> tasks,
        RelayConfig config,
        ITestRunner testRunner,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken = default,
        Func<string, IRelayEventSink>? eventSinkFactory = null,
        IEnvironmentAccessor? environmentAccessor = null,
        SandboxHost? sandboxHost = null)
    {
        var taskList = tasks.ToList();
        if (taskList.Count == 0)
            return [];

        var maxConcurrency = Math.Max(1, config.MaxPlanConcurrency);
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var runId = $"plan-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var results = new List<(string TaskId, RelayTaskOutcome Outcome)>();

        // Prune any leftover worktrees from a prior crashed drain.
        await PlanningWorktree.PruneLeftoversAsync(mainRootPath, runId, gitInvoker, cancellationToken);

        // Fire all planning tasks concurrently, gated by the semaphore.
        await Task.WhenAll(taskList.Select(t => PlanOneAsync(
            mainRootPath, t.TaskId, t.RunnerFactory, testRunner, runId,
            semaphore, results, eventSinkFactory, environmentAccessor, sandboxHost, gitInvoker, cancellationToken)));

        // Return in input order.
        return results
            .OrderBy(r => taskList.FindIndex(x => x.TaskId == r.TaskId))
            .ToList();
    }

    private static async Task PlanOneAsync(
        string mainRootPath,
        string taskId,
        Func<IRelayEventSink, ISubagentRunner> runnerFactory,
        ITestRunner testRunner,
        string runId,
        SemaphoreSlim semaphore,
        List<(string TaskId, RelayTaskOutcome Outcome)> results,
        Func<string, IRelayEventSink>? eventSinkFactory,
        IEnvironmentAccessor? environmentAccessor,
        SandboxHost? sandboxHost,
        IGitInvoker gitInvoker,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var outcome = await PlanOneTaskAsync(
                mainRootPath, taskId, runnerFactory, testRunner, runId,
                eventSinkFactory, environmentAccessor, sandboxHost, gitInvoker, ct);
            lock (results)
                results.Add((taskId, outcome));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The drain was cancelled: this task never reached a verdict, so it
            // records none and stays pending for the next drain.
        }
        catch (Exception ex)
        {
            // Per-task exception handling: a single planning task that throws
            // must not crash the entire drain. Record as Failed so the caller
            // can report it, matching the serial behavior.
            var failed = new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Failed,
                null, null, $"planning exception: {ex.Message}");
            lock (results)
                results.Add((taskId, failed));
            DrainSummaryLog.Write(mainRootPath, runId, taskId, "plan",
                "exception", ex.Message);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<RelayTaskOutcome> PlanOneTaskAsync(
        string mainRootPath,
        string taskId,
        Func<IRelayEventSink, ISubagentRunner> runnerFactory,
        ITestRunner testRunner,
        string runId,
        Func<string, IRelayEventSink>? eventSinkFactory,
        IEnvironmentAccessor? environmentAccessor,
        SandboxHost? sandboxHost,
        IGitInvoker gitInvoker,
        CancellationToken ct)
    {
        string? worktreePath = null;
        try
        {
            DrainSummaryLog.Write(mainRootPath, runId, taskId, "plan", "start");
            // Fresh token, like the removal below: a half-added worktree is a git
            // admin entry pointing at a directory that was never checked out.
            worktreePath = await PlanningWorktree.CreateAsync(mainRootPath, taskId, runId, gitInvoker, CancellationToken.None);
            // The detached checkout omits the (normally git-ignored) per-repo
            // config, so provide it from the source repo before the driver loads
            // config from the worktree — otherwise stage 1 flags "config not found".
            PlanningWorktree.CopyConfigIntoWorktree(mainRootPath, worktreePath);
            await PlanningWorktree.CopyTaskSpecIntoWorktree(mainRootPath, worktreePath, taskId, ct);

            // Each planning task gets its OWN event sink to avoid log interleaving.
            // When an observable factory is provided (GUI drain), live progress
            // reaches the UI; otherwise falls back to a silent null sink.
            var observableSink = eventSinkFactory?.Invoke(taskId)
                ?? new NullRelayEventSink();
            var fileSink = new FileRelayEventSink(
                Path.Combine(worktreePath, ".relay", taskId, "run.log"));
            var sink = new CompositeRelayEventSink(observableSink, fileSink);
            // The agent is built from THIS sink, so its trace stream lands in the same
            // run.log the driver writes rather than in the GUI alone.
            var dependencies = new RelayDriverDependencies(
                runnerFactory(sink), testRunner, sink, gitInvoker, environmentAccessor, SandboxHost: sandboxHost);
            var options = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
            var driver = new RelayDriver(dependencies, options);
            await driver.OverlayCheckoutDependenciesAsync(mainRootPath, worktreePath, taskId, runId, ct);

            var outcome = await driver.RunTaskAsync(worktreePath, taskId, ct);

            // Copy artifacts back regardless of outcome — even flagged tasks
            // need their NEEDS-REVIEW marker and partial status in the main repo.
            // A cancelled planning run is the exception: it reached no verdict, so
            // copying its wind-down back would mark a task the operator only stopped.
            if (!ct.IsCancellationRequested)
                PlanningWorktree.CopyArtifactsBack(mainRootPath, worktreePath, taskId);

            DrainSummaryLog.Write(mainRootPath, runId, taskId, "plan",
                outcome.Status == RelayTaskOutcomeStatus.Flagged
                    ? "flagged"
                    : "done(stage4)",
                outcome.Reason);
            return outcome;
        }
        finally
        {
            // Fresh token: a cancelled drain must still take its worktrees with it.
            if (worktreePath is not null)
            {
                // The overlay may have linked the checkout's large dependencies in here.
                WorktreeLinks.UnlinkAll(worktreePath);
                await PlanningWorktree.RemoveAsync(mainRootPath, worktreePath, gitInvoker, CancellationToken.None);
            }
        }
    }
}
