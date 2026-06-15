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
    /// <param name="tasks">Task ids paired with the subagent runner that drives each.</param>
    /// <param name="config">Drive configuration; <c>MaxPlanConcurrency</c> bounds parallelism.</param>
    /// <param name="testRunner">Test runner used by the planning stages.</param>
    /// <param name="cancellationToken">Cancels all in-flight planning work.</param>
    /// <param name="eventSinkFactory">
    /// Optional factory that creates an observable <see cref="IRelayEventSink"/>
    /// per task so live progress reaches the GUI. When null, a
    /// <see cref="NullRelayEventSink"/> is used (no live progress).
    /// </param>
    public static async Task<List<(string TaskId, RelayTaskOutcome Outcome)>> RunPlanPhaseAsync(
        string mainRootPath,
        IEnumerable<(string TaskId, ISubagentRunner Runner)> tasks,
        RelayConfig config,
        ITestRunner testRunner,
        CancellationToken cancellationToken = default,
        Func<string, IRelayEventSink>? eventSinkFactory = null)
    {
        var taskList = tasks.ToList();
        if (taskList.Count == 0)
            return [];

        var maxConcurrency = Math.Max(1, config.MaxPlanConcurrency);
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var runId = $"plan-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var results = new List<(string TaskId, RelayTaskOutcome Outcome)>();

        // Prune any leftover worktrees from a prior crashed drain.
        await PlanningWorktree.PruneLeftoversAsync(mainRootPath, runId, cancellationToken);

        // Fire all planning tasks concurrently, gated by the semaphore.
        await Task.WhenAll(taskList.Select(t => PlanOneAsync(
            mainRootPath, t.TaskId, t.Runner, config, testRunner, runId,
            semaphore, results, eventSinkFactory, cancellationToken)));

        // Return in input order.
        return results
            .OrderBy(r => taskList.FindIndex(x => x.TaskId == r.TaskId))
            .ToList();
    }

    private static async Task PlanOneAsync(
        string mainRootPath,
        string taskId,
        ISubagentRunner runner,
        RelayConfig config,
        ITestRunner testRunner,
        string runId,
        SemaphoreSlim semaphore,
        List<(string TaskId, RelayTaskOutcome Outcome)> results,
        Func<string, IRelayEventSink>? eventSinkFactory,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var outcome = await PlanOneTaskAsync(
                mainRootPath, taskId, runner, testRunner, runId,
                eventSinkFactory, ct);
            lock (results)
                results.Add((taskId, outcome));
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
        ISubagentRunner runner,
        ITestRunner testRunner,
        string runId,
        Func<string, IRelayEventSink>? eventSinkFactory,
        CancellationToken ct)
    {
        string? worktreePath = null;
        try
        {
            DrainSummaryLog.Write(mainRootPath, runId, taskId, "plan", "start");
            worktreePath = await PlanningWorktree.CreateAsync(mainRootPath, taskId, runId, ct);

            // Each planning task gets its OWN event sink to avoid log interleaving.
            // When an observable factory is provided (GUI drain), live progress
            // reaches the UI; otherwise falls back to a silent null sink.
            var observableSink = eventSinkFactory?.Invoke(taskId)
                ?? new NullRelayEventSink();
            var fileSink = new FileRelayEventSink(
                Path.Combine(worktreePath, ".relay", taskId, "run.log"));
            var sink = new CompositeRelayEventSink(observableSink, fileSink);
            var dependencies = new RelayDriverDependencies(runner, testRunner, sink);
            var options = new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4);
            var driver = new RelayDriver(dependencies, options);

            var outcome = await driver.RunTaskAsync(worktreePath, taskId, ct);

            // Copy artifacts back regardless of outcome — even flagged tasks
            // need their NEEDS-REVIEW marker and partial status in the main repo.
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
            if (worktreePath is not null)
                await PlanningWorktree.RemoveAsync(mainRootPath, worktreePath, ct);
        }
    }
}
