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

    /// <summary>Two-phase constructor: when plan factories are non-null,
    /// DrainAsync runs planning in parallel worktrees before serial execute.</summary>
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

    private string RootPath { get; }
    public ObservableCollection<RelayTaskItem> Tasks { get; } = [];
    public RelayQueueState State { get; private set; } = RelayQueueState.Idle;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Init.RelayGitignoreWriter.EnsureWritten(RootPath);
        State = RelayQueueState.Refreshing;
        Tasks.Clear();
        // Seed from the persisted manual order so a headless/CLI drain runs in the
        // user's saved order — not the repository's alphabetical baseline. The same
        // store backs the GUI's visible queue, so display and run order stay aligned.
        var listed = await _repository.ListPendingAsync(cancellationToken);
        foreach (var task in new TaskOrderStore(RootPath).Apply(listed, task => task.Id))
            Tasks.Add(task);
        State = RelayQueueState.Idle;
    }

    public void RequestPause()
    {
        _pauseRequested = true;
        if (State == RelayQueueState.Running) State = RelayQueueState.PauseRequested;
    }

    // ReSharper disable once UnusedMember.Global — public symmetric reorder API;
    // the counterpart MoveDown is exercised by RelayQueueControllerTests.
    public void MoveUp(string taskId)
    {
        var index = IndexOf(taskId);
        if (index > 0) Tasks.Move(index, index - 1);
    }

    public void MoveDown(string taskId)
    {
        var index = IndexOf(taskId);
        if (index >= 0 && index < Tasks.Count - 1) Tasks.Move(index, index + 1);
    }

    /// <summary>
    /// Stable-reorders <see cref="Tasks"/> to match <paramref name="orderedIds"/>.
    /// Ids absent from <paramref name="orderedIds"/> keep their original relative
    /// order at the end. In-memory only — order is never persisted. Used by the
    /// GUI drain to align this controller with the app's visible order.
    /// </summary>
    public void ApplyOrder(IReadOnlyList<string> orderedIds)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedIds.Count; i++) rank.TryAdd(orderedIds[i], i);
        var sorted = Tasks
            .Select((t, i) => (t, key: rank.GetValueOrDefault(t.Id, int.MaxValue), orig: i))
            .OrderBy(x => x.key).ThenBy(x => x.orig)
            .Select(x => x.t).ToList();
        Tasks.Clear();
        foreach (var t in sorted) Tasks.Add(t);
    }

    public async Task<IReadOnlyList<RelayTaskOutcome>> DrainAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RelayTaskOutcome>();
        var circuitBreaker = new DrainCircuitBreaker();
        _pauseRequested = false;
        DrainCircuitBreaker.ClearHaltMarker(RootPath);
        State = RelayQueueState.Running;

        // Per-drain CTS; pause/stop cancels in-flight planning.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var drainRunId = $"drain-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var queue = Tasks.Where(task => !task.NeedsReview).ToList();

        // Promote configResult so Phase 2 reads TasksDir without a second parse.
        RelayConfigResult? configResult = null;

        // ── Phase 1: parallel planning ──
        if (_planSubagentRunnerFactory is not null && _planTestRunner is not null)
        {
            configResult = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
            if (configResult.IsRunnable)
            {
                // Tasks needing planning (stages 1–4 not all Done).
                var needsPlan = new List<(string TaskId, ISubagentRunner Runner)>();
                foreach (var task in queue)
                    if (!StagesOneThroughFourAreDone(task.Id))
                        needsPlan.Add((task.Id, _planSubagentRunnerFactory!(task.Id)));

                if (needsPlan.Count > 0)
                {
                    if (_lifecycle is not null)
                        foreach (var (taskId, _) in needsPlan)
                            _lifecycle.OnPlanningStarted?.Invoke(taskId);

                    foreach (var (taskId, _) in needsPlan)
                        DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan", "start");

                    var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
                        RootPath, needsPlan, configResult.Config, _planTestRunner, drainCts.Token, _planEventSinkFactory);

                    foreach (var (taskId, outcome) in planResults)
                    {
                        if (outcome.Status is RelayTaskOutcomeStatus.Flagged
                            or RelayTaskOutcomeStatus.Failed)
                        {
                            results.Add(outcome);

                            var queueTask = queue.FirstOrDefault(t => t.Id == taskId);
                            if (queueTask is not null) queue.Remove(queueTask);

                            DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan",
                                outcome.Status == RelayTaskOutcomeStatus.Flagged ? "flagged" : "failed", outcome.Reason);
                            _lifecycle?.OnPlanningCompleted?.Invoke(taskId, outcome.Status);

                            if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                            {
                                await ResetAndLogAsync(taskId, configResult?.Config?.TasksDir, drainRunId, "plan", drainCts.Token);
                                try { WriteNeedsReviewMarker(taskId, outcome.Reason ?? "Needs review"); }
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
                            _lifecycle?.OnPlanningCompleted?.Invoke(taskId, outcome.Status);
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

            var task = queue[0];
            queue.RemoveAt(0);

            _lifecycle?.OnExecuteStarted?.Invoke(task.Id);
            DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", "start");

            RelayTaskOutcome outcome;
            try { outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { State = RelayQueueState.Failed; return results; }
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

            _lifecycle?.OnExecuteCompleted?.Invoke(task.Id, outcome.Status);

            if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
            {
                var tasksDir = configResult?.Config?.TasksDir
                    ?? (await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken)).Config.TasksDir;
                await ResetAndLogAsync(outcome.TaskId, tasksDir, drainRunId, "execute", cancellationToken);
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

    private bool StagesOneThroughFourAreDone(string taskId)
    {
        var status = StageStatusRecord.Read(Path.Combine(RootPath, ".relay", taskId));
        return status.Count >= 4 && status.Take(4).All(e => e.Status == "Done");
    }

    private void WriteNeedsReviewMarker(string taskId, string reason)
    {
        var dir = Path.Combine(RootPath, ".relay", taskId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "NEEDS-REVIEW"), reason + Environment.NewLine);
    }

    private async Task ResetAndLogAsync(string taskId, string? tasksDir, string drainRunId, string phase, CancellationToken ct)
    {
        var gitInvoker = new GitInvoker();
        try { await WorktreeResetter.ResetAsync(RootPath, taskId, tasksDir, ct, gitInvoker); }
        catch (Exception ex) { DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase, "reset-failed", ex.Message); }
    }

    private int IndexOf(string taskId)
    {
        for (var i = 0; i < Tasks.Count; i++)
            if (string.Equals(Tasks[i].Id, taskId, StringComparison.Ordinal)) return i;
        return -1;
    }
}
