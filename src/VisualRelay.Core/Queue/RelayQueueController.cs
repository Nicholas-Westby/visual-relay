using System.Collections.ObjectModel;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed class RelayQueueController
{
    private readonly IRelayTaskRunner _runner;
    private readonly RelayTaskRepository _repository;
    private readonly Func<string, ISubagentRunner>? _planSubagentRunnerFactory;
    private readonly ITestRunner? _planTestRunner;
    private readonly Func<string, IRelayEventSink>? _planEventSinkFactory;
    private readonly DrainLifecycleCallbacks? _lifecycle;
    private bool _pauseRequested;

    /// <summary>
    /// Legacy constructor: serial-only drain (no parallel planning).
    /// </summary>
    public RelayQueueController(string rootPath, IRelayTaskRunner runner)
        : this(rootPath, runner, null, null, null, null)
    {
    }

    /// <summary>
    /// Two-phase constructor: when <paramref name="planSubagentRunnerFactory"/> and
    /// <paramref name="planTestRunner"/> are non-null, DrainAsync runs planning
    /// stages (1–4) in parallel worktrees before the serial execute phase.
    /// The factory receives a task id so each task gets its own runner instance,
    /// enabling per-task behavior (e.g. only one task flags).
    /// </summary>
    public RelayQueueController(
        string rootPath,
        IRelayTaskRunner runner,
        Func<string, ISubagentRunner>? planSubagentRunnerFactory = null,
        ITestRunner? planTestRunner = null,
        Func<string, IRelayEventSink>? planEventSinkFactory = null,
        DrainLifecycleCallbacks? lifecycle = null)
    {
        RootPath = rootPath;
        _runner = runner;
        _repository = new RelayTaskRepository(rootPath);
        _planSubagentRunnerFactory = planSubagentRunnerFactory;
        _planTestRunner = planTestRunner;
        _planEventSinkFactory = planEventSinkFactory;
        _lifecycle = lifecycle;
    }

    public string RootPath { get; }
    public ObservableCollection<RelayTaskItem> Tasks { get; } = [];
    public RelayQueueState State { get; private set; } = RelayQueueState.Idle;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Initialized-before-this-policy repos get the diagnostics gitignore
        // on their next run; no-op when .relay is absent or the file exists.
        Init.RelayGitignoreWriter.EnsureWritten(RootPath);

        State = RelayQueueState.Refreshing;
        Tasks.Clear();
        foreach (var task in await _repository.ListPendingAsync(cancellationToken))
        {
            Tasks.Add(task);
        }

        State = RelayQueueState.Idle;
    }

    public void RequestPause()
    {
        _pauseRequested = true;
        if (State == RelayQueueState.Running)
        {
            State = RelayQueueState.PauseRequested;
        }
    }

    public void MoveUp(string taskId)
    {
        var index = IndexOf(taskId);
        if (index > 0)
        {
            Tasks.Move(index, index - 1);
        }
    }

    public void MoveDown(string taskId)
    {
        var index = IndexOf(taskId);
        if (index >= 0 && index < Tasks.Count - 1)
        {
            Tasks.Move(index, index + 1);
        }
    }

    public async Task<IReadOnlyList<RelayTaskOutcome>> DrainAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RelayTaskOutcome>();
        var circuitBreaker = new DrainCircuitBreaker();
        _pauseRequested = false;
        DrainCircuitBreaker.ClearHaltMarker(RootPath);
        State = RelayQueueState.Running;

        // Per-drain CTS so pause/stop cancels in-flight planning tasks.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var drainRunId = $"drain-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        // Snapshot the queue at drain start (tasks added mid-drain are excluded).
        var queue = Tasks.Where(task => !task.NeedsReview).ToList();

        // ── Phase 1: parallel planning (stages 1–4) ──
        if (_planSubagentRunnerFactory is not null && _planTestRunner is not null)
        {
            var configResult = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
            if (configResult.IsRunnable)
            {
                // Determine which tasks still need planning (status 1–4 not all Done).
                var needsPlan = new List<(string TaskId, ISubagentRunner Runner)>();
                foreach (var task in queue)
                {
                    if (!StagesOneThroughFourAreDone(task.Id))
                        needsPlan.Add((task.Id, _planSubagentRunnerFactory!(task.Id)));
                }

                if (needsPlan.Count > 0)
                {
                    // Notify lifecycle: each task entering planning.
                    if (_lifecycle is not null)
                    {
                        foreach (var (taskId, _) in needsPlan)
                            _lifecycle.OnPlanningStarted?.Invoke(taskId);
                    }

                    foreach (var (taskId, _) in needsPlan)
                        DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan", "start");

                    var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
                        RootPath, needsPlan, configResult.Config, _planTestRunner,
                        drainCts.Token, _planEventSinkFactory);

                    foreach (var (taskId, outcome) in planResults)
                    {
                        if (outcome.Status is RelayTaskOutcomeStatus.Flagged
                            or RelayTaskOutcomeStatus.Failed)
                        {
                            results.Add(outcome);

                            var queueTask = queue.FirstOrDefault(t => t.Id == taskId);
                            if (queueTask is not null)
                                queue.Remove(queueTask);

                            DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan",
                                outcome.Status == RelayTaskOutcomeStatus.Flagged
                                    ? "flagged" : "failed", outcome.Reason);
                            _lifecycle?.OnPlanningCompleted?.Invoke(taskId, outcome.Status);

                            if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                            {
                                WriteNeedsReviewMarker(taskId, outcome.Reason ?? "Needs review");
                                var idx = IndexOf(taskId);
                                if (idx >= 0 && queueTask is not null)
                                {
                                    Tasks.RemoveAt(idx);
                                    Tasks.Add(queueTask with { ReviewReason = outcome.Reason ?? "Needs review" });
                                }
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
                            _lifecycle?.OnPlanningCompleted?.Invoke(taskId, outcome.Status);
                        }
                    }

                    if (_pauseRequested)
                    {
                        drainCts.Cancel();
                        State = RelayQueueState.Paused;
                        // Add Planned outcomes for tasks that were planned but won't
                        // proceed to Phase 2, so the GUI can report plannedCount.
                        foreach (var planned in queue)
                            results.Add(new RelayTaskOutcome(planned.Id, RelayTaskOutcomeStatus.Planned, null, null, null));
                        return results;
                    }
                }
            }
        }

        // ── Phase 2: serial execute (stages 5–11) ──
        while (queue.Count > 0)
        {
            if (_pauseRequested)
            {
                State = RelayQueueState.Paused;
                return results;
            }

            var task = queue[0];
            queue.RemoveAt(0);

            _lifecycle?.OnExecuteStarted?.Invoke(task.Id);
            DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", "start");

            RelayTaskOutcome outcome;
            try
            {
                outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                State = RelayQueueState.Failed;
                return results;
            }
            catch (Exception ex)
            {
                // Convert unhandled exception to a flagged outcome so the drain continues.
                outcome = new RelayTaskOutcome(task.Id, RelayTaskOutcomeStatus.Flagged,
                    null, null, $"unhandled exception: {ex.GetType().Name}: {ex.Message}");
            }
            results.Add(outcome);

            var taskIdx = IndexOf(task.Id);
            if (taskIdx >= 0)
                Tasks.RemoveAt(taskIdx);

            var milestone = outcome.Status switch
            {
                RelayTaskOutcomeStatus.Committed => "committed",
                RelayTaskOutcomeStatus.Flagged => "flagged",
                _ => "failed"
            };
            DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", milestone,
                outcome.Status == RelayTaskOutcomeStatus.Committed ? outcome.CommitSha : outcome.Reason);

            _lifecycle?.OnExecuteCompleted?.Invoke(task.Id, outcome.Status);

            if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
            {
                try { WriteNeedsReviewMarker(outcome.TaskId, outcome.Reason ?? "Needs review"); }
                catch { DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", "exception", "WriteNeedsReviewMarker failed"); }
                Tasks.Add(task with { ReviewReason = outcome.Reason ?? "Needs review" });
            }

            if (circuitBreaker.ShouldHalt(RootPath, outcome))
            {
                State = outcome.Reason?.StartsWith("commit rejected:", StringComparison.OrdinalIgnoreCase) == true
                    ? RelayQueueState.Failed : RelayQueueState.ReviewNeeded;
                return results;
            }
        }

        State = results.Any(r => r.Status == RelayTaskOutcomeStatus.Flagged)
            ? RelayQueueState.ReviewNeeded
            : RelayQueueState.Completed;
        return results;
    }

    /// <summary>
    /// Returns true when all stages 1–4 are marked Done in the task's status.json.
    /// </summary>
    private bool StagesOneThroughFourAreDone(string taskId)
    {
        var taskDir = Path.Combine(RootPath, ".relay", taskId);
        var status = StageStatusRecord.Read(taskDir);
        return status.Count >= 4
            && status.Take(4).All(e => e.Status == "Done");
    }

    private void WriteNeedsReviewMarker(string taskId, string reason)
    {
        var relayDirectory = Path.Combine(RootPath, ".relay", taskId);
        Directory.CreateDirectory(relayDirectory);
        File.WriteAllText(Path.Combine(relayDirectory, "NEEDS-REVIEW"), reason + Environment.NewLine);
    }

    private int IndexOf(string taskId)
    {
        for (var i = 0; i < Tasks.Count; i++)
        {
            if (string.Equals(Tasks[i].Id, taskId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
