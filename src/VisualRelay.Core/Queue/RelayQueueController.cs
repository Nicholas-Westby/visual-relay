using System.Collections.ObjectModel;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed partial class RelayQueueController
{
    private readonly IRelayTaskRunner _runner;
    private readonly RelayTaskRepository _repository;
    private readonly Func<string, IRelayEventSink, ISubagentRunner>? _planSubagentRunnerFactory;
    private readonly ITestRunner? _planTestRunner;
    private readonly Func<string, IRelayEventSink>? _planEventSinkFactory;
    private readonly DrainLifecycleCallbacks? _lifecycle;
    private readonly IEnvironmentAccessor? _environmentAccessor;
    private readonly SandboxHost? _sandboxHost;
    private readonly IGitInvoker? _gitInvoker;
    private Func<IReadOnlyList<RelayTaskItem>>? _externalTaskSource;
    private bool _pauseRequested;
    private HashSet<string>? _drainSeenIds;
    /// <summary>Two-phase constructor: when plan factories are non-null, DrainAsync runs planning in
    /// parallel worktrees before serial execute. <paramref name="environmentAccessor"/> and <paramref name="sandboxHost"/>
    /// reach each planning driver's vr-guard profile self-heal, <paramref name="gitInvoker"/> planning and flagged-task
    /// worktree reset: null in production (real env, this machine, real git); temp XDG, local host and a sim in tests.</summary>
    public RelayQueueController(
        string rootPath,
        IRelayTaskRunner runner,
        Func<string, IRelayEventSink, ISubagentRunner>? planSubagentRunnerFactory = null,
        ITestRunner? planTestRunner = null,
        Func<string, IRelayEventSink>? planEventSinkFactory = null,
        DrainLifecycleCallbacks? lifecycle = null,
        IEnvironmentAccessor? environmentAccessor = null,
        IGitInvoker? gitInvoker = null,
        SandboxHost? sandboxHost = null)
    {
        RootPath = rootPath;
        _runner = runner;
        _repository = new RelayTaskRepository(rootPath);
        _planSubagentRunnerFactory = planSubagentRunnerFactory;
        _planTestRunner = planTestRunner;
        _planEventSinkFactory = planEventSinkFactory;
        _lifecycle = lifecycle;
        _environmentAccessor = environmentAccessor;
        _sandboxHost = sandboxHost;
        _gitInvoker = gitInvoker;
    }

    private string RootPath { get; }
    public ObservableCollection<RelayTaskItem> Tasks { get; } = [];
    public RelayQueueState State { get; private set; } = RelayQueueState.Idle;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Init.RelayGitignoreWriter.EnsureWritten(RootPath);
        State = RelayQueueState.Refreshing;
        Tasks.Clear();
        // Seed from the persisted manual order so headless/CLI drains use the
        // user's saved order, not the alphabetical baseline.
        var listed = await _repository.ListAsync(cancellationToken: cancellationToken);
        foreach (var task in new TaskOrderStore(RootPath).Apply(listed, task => task.Id))
            Tasks.Add(task);
        State = RelayQueueState.Idle;
    }

    public void RequestPause()
    {
        _pauseRequested = true;
        if (State == RelayQueueState.Running) State = RelayQueueState.PauseRequested;
    }

    public async Task<IReadOnlyList<RelayTaskOutcome>> DrainAsync(
        CancellationToken cancellationToken = default,
        RunAllMode mode = RunAllMode.Standard)
    {
        var results = new List<RelayTaskOutcome>();
        var circuitBreaker = new DrainCircuitBreaker();
        _pauseRequested = false;
        DrainCircuitBreaker.ClearHaltMarker(RootPath);
        State = RelayQueueState.Running;

        // Per-drain CTS; pause/stop cancels in-flight planning.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var drainRunId = $"drain-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var queue = BuildDrainQueue(Tasks, mode, RootPath, drainRunId);
        _drainSeenIds = new HashSet<string>(queue.Select(t => t.Id), StringComparer.Ordinal);
        var seenIds = _drainSeenIds;
        var firstPass = true;

        // Promote configResult so Phase 2 reads TasksDir without a second parse.
        RelayConfigResult? configResult = null;
        try
        {
            var skipPlanning = mode is RunAllMode.Sequential or RunAllMode.RestartBetweenTasks;

            while (true)
            {
                if (!firstPass)
                {
                    SyncExternalTasks();
                    var newTasks = CollectNewTasks(Tasks, seenIds);
                    if (newTasks.Count == 0) break;
                    foreach (var nt in newTasks) seenIds.Add(nt.Id);
                    queue = newTasks;
                }
                firstPass = false;

                if (!skipPlanning && _planSubagentRunnerFactory is not null && _planTestRunner is not null)
                {
                    configResult = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
                    if (configResult.IsRunnable)
                    {
                        // Tasks needing planning (stages 1–4 not all Done).
                        var needsPlan = new List<(string TaskId, Func<IRelayEventSink, ISubagentRunner> Factory)>();
                        // Each agent is built later, from the sink its own worktree gets.
                        foreach (var task in queue.Where(t => !StagesOneThroughFourAreDone(t.Id)))
                            needsPlan.Add((task.Id, sink => _planSubagentRunnerFactory!(task.Id, sink)));

                        if (needsPlan.Count > 0)
                        {
                            if (_lifecycle is not null)
                                foreach (var (taskId, _) in needsPlan)
                                    _lifecycle.OnPlanningStarted?.Invoke(taskId);

                            foreach (var (taskId, _) in needsPlan)
                                DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan", "start");

                            var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
                                RootPath, needsPlan, configResult.Config, _planTestRunner, _gitInvoker ?? new GitInvoker(), drainCts.Token,
                                _planEventSinkFactory, _environmentAccessor, _sandboxHost);

                            // Cancelled planning reached no verdict and copied nothing
                            // back, so every task stays pending, unmarked and unqueued.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                State = RelayQueueState.Cancelled;
                                return results;
                            }

                            foreach (var (taskId, outcome) in planResults)
                            {
                                if (outcome.Status is RelayTaskOutcomeStatus.Flagged
                                    or RelayTaskOutcomeStatus.Failed)
                                {
                                    results.Add(outcome);

                                    var queueTask = queue.FirstOrDefault(t => t.Id == taskId);
                                    if (queueTask is not null) queue.Remove(queueTask);
                                    seenIds.Add(taskId);

                                    DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan",
                                        outcome.Status == RelayTaskOutcomeStatus.Flagged ? "flagged" : "failed", outcome.Reason);
                                    _lifecycle?.OnPlanningCompleted?.Invoke(taskId, outcome);

                                    if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                                    {
                                        await ResetAndLogAsync(taskId, configResult?.Config?.TasksDir ?? (await RelayConfigLoader.TryLoadAsync(RootPath, drainCts.Token)).Config.TasksDir, drainRunId, "plan", drainCts.Token);
                                        try { await WriteNeedsReviewMarkerAsync(taskId, outcome.Reason ?? "Needs review"); }
                                        catch { DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan", "exception", "WriteNeedsReviewMarker failed"); }
                                        var idx = IndexOf(taskId);
                                        if (idx >= 0 && queueTask is not null)
                                        { Tasks.RemoveAt(idx); Tasks.Add(queueTask with { ReviewReason = outcome.Reason ?? "Needs review" }); }
                                    }

                                    if (circuitBreaker.ShouldHalt(RootPath, outcome))
                                    {
                                        State = outcome.Reason?.StartsWith("commit rejected:", StringComparison.OrdinalIgnoreCase) == true
                                            ? RelayQueueState.Failed : RelayQueueState.ReviewNeeded;
                                        drainCts.Cancel();
                                        return results;
                                    }
                                }
                                else
                                {
                                    // Planned tasks stay in queue for Phase 2 execution.
                                    DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan", "done(stage4)");
                                    _lifecycle?.OnPlanningCompleted?.Invoke(taskId, outcome);
                                }
                            }

                            if (_pauseRequested)
                            {
                                drainCts.Cancel();
                                State = RelayQueueState.Paused;
                                foreach (var planned in queue)
                                    results.Add(new RelayTaskOutcome(planned.Id, RelayTaskOutcomeStatus.Planned, null, null, null));
                                return results;
                            }
                        }
                    }
                }

                // ── Phase 2: serial execute ──
                while (queue.Count > 0)
                {
                    if (_pauseRequested)
                    {
                        State = RelayQueueState.Paused;
                        return results;
                    }

                    // A cancelled drain starts nothing new; the rest of the queue
                    // stays pending exactly as the operator left it.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        State = RelayQueueState.Cancelled;
                        return results;
                    }

                    var task = queue[0];
                    queue.RemoveAt(0);
                    seenIds.Add(task.Id);

                    _lifecycle?.OnExecuteStarted?.Invoke(task.Id);
                    DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", "start");

                    RelayTaskOutcome outcome;
                    try { outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // A runner that throws the cancel instead of winding down itself
                        // still leaves a task needing review, like the driver's own path.
                        outcome = new RelayTaskOutcome(task.Id, RelayTaskOutcomeStatus.Flagged,
                            null, null, RelayDriver.CancelledReason);
                    }
                    catch (Exception ex)
                    {
                        outcome = new RelayTaskOutcome(task.Id, RelayTaskOutcomeStatus.Flagged,
                            null, null, $"unhandled exception: {ex.GetType().Name}: {ex.Message}");
                    }
                    results.Add(outcome);

                    var taskIdx = IndexOf(task.Id);
                    if (taskIdx >= 0) Tasks.RemoveAt(taskIdx);

                    var milestone = outcome.Status switch
                    {
                        RelayTaskOutcomeStatus.Committed => "committed",
                        RelayTaskOutcomeStatus.Flagged => "flagged",
                        _ => "failed"
                    };
                    DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", milestone,
                        outcome.Status == RelayTaskOutcomeStatus.Committed ? outcome.CommitSha : outcome.Reason);

                    _lifecycle?.OnExecuteCompleted?.Invoke(task.Id, outcome);

                    if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                    {
                        // Tidying up runs on a fresh token: after a cancel the drain's own
                        // token is spent, and a half-done reset is worse than none.
                        var tasksDir = configResult?.Config?.TasksDir
                            ?? (await RelayConfigLoader.TryLoadAsync(RootPath, CancellationToken.None)).Config.TasksDir;
                        await ResetAndLogAsync(outcome.TaskId, tasksDir, drainRunId, "execute", CancellationToken.None);
                        try { await WriteNeedsReviewMarkerAsync(outcome.TaskId, outcome.Reason ?? "Needs review"); }
                        catch { DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", "exception", "WriteNeedsReviewMarker failed"); }
                        Tasks.Add(task with { ReviewReason = outcome.Reason ?? "Needs review" });
                    }

                    // A cancel outranks what follows: it must not trip the circuit
                    // breaker, nor hand off a restart into the queue it just stopped.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        State = RelayQueueState.Cancelled;
                        return results;
                    }

                    // Sequential / RestartBetweenTasks: check for new tasks at
                    // each task boundary.
                    if (skipPlanning)
                        queue = CollectAndMergeNewTasksAtBoundary(seenIds, queue);

                    // RestartBetweenTasks: after a committed task, write handoff
                    // and stop the drain; flagged tasks continue in-process.
                    if (TryRestartBetweenTasks(mode, outcome, task.Id, drainRunId,
                            queue.Count))
                        return results;

                    if (circuitBreaker.ShouldHalt(RootPath, outcome))
                    {
                        State = outcome.Reason?.StartsWith("commit rejected:", StringComparison.OrdinalIgnoreCase) == true
                            ? RelayQueueState.Failed : RelayQueueState.ReviewNeeded;
                        return results;
                    }
                }
                if (skipPlanning) break;
            }
            // No-progress guard for RestartBetweenTasks: consume stale handoff.
            ConsumeHandoffIfRestartMode(mode);

            State = results.Any(r => r.Status == RelayTaskOutcomeStatus.Flagged)
                ? RelayQueueState.ReviewNeeded
                : RelayQueueState.Completed;
            return results;
        }
        finally { _drainSeenIds = null; }
    }
}
