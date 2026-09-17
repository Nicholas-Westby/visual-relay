using VisualRelay.App.Services;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

// ReSharper disable once UnusedType.Global — partial of MainWindowViewModel
public partial class MainWindowViewModel
{
    /// <summary>
    /// The environment seam every runner-construction site reads, falling back
    /// to the real process environment when nothing was injected.
    /// </summary>
    private IEnvironmentAccessor Env => EnvironmentAccessor ?? new SystemEnvironmentAccessor();

    /// <summary>Builds the agent for a stage, publishing to the caller's sink.</summary>
    /// <param name="config">The repository's relay configuration.</param>
    /// <param name="sink">Where the agent's stage and trace events go.</param>
    /// <returns>The runner to use.</returns>
    private ISubagentRunner CreateSubagentRunner(RelayConfig config, IRelayEventSink sink) =>
        SubagentRunnerFactory.Create(config, sink, Env, VerboseSandboxDiagnostics);

    /// <summary>Builds a sandboxed test runner over the configured timeout.</summary>
    /// <param name="config">The repository's relay configuration.</param>
    /// <returns>A fresh runner; callers do not share instances.</returns>
    private SandboxedTestRunner CreateSandboxedTestRunner(RelayConfig config) =>
        new(
            new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)),
            config,
            VerboseSandboxDiagnostics);

    /// <summary>
    /// Test seam: builds the runner a single-task run drives. Null in production,
    /// where <see cref="RunOneAsync"/> builds the real <see cref="RelayDriver"/> over
    /// the stage agents; a test installs a fake to exercise the run's wiring.
    /// </summary>
    internal Func<RelayConfig, IRelayEventSink, IRelayTaskRunner>? SingleRunTaskRunnerFactory { get; set; }

    private async Task RunOneAsync(TaskRowViewModel task, CancellationToken cancellationToken, bool resume = false)
    {
        if (resume) { ResetStages(task.Id); } else { ResetStages(); }
        ClearLogState();
        StatusText = $"Running {task.Id}";
        // BeginRunningTask clears the stale detail-pane error when this is the
        // selected task (run start ⇒ the prior run's error no longer applies).
        BeginRunningTask(task);
        NotifyPauseStateChanged();
        var config = await RelayConfigLoader.LoadAsync(RootPath, cancellationToken);
        var observable = new ObservableRelayEventSink(HandleRelayEvent);
        var fileSink = new FileRelayEventSink(Path.Combine(RootPath, ".relay", task.Id, "run.log"));
        var sink = new CompositeRelayEventSink(observable, fileSink);
        var runner = SingleRunTaskRunnerFactory?.Invoke(config, sink) ?? CreateSingleRunDriver(config, sink, resume);
        try
        {
            var outcome = await runner.RunTaskAsync(RootPath, task.Id, cancellationToken);
            // A commit ends the halt. The drain clears the marker at its start, but
            // run-selected and resume build a driver directly and never pass through
            // it, so a halt from a rejected commit stayed in /state after the resume
            // that fixed it, and across a relaunch. A flagged or cancelled run leaves
            // the marker alone: nothing has been proven yet.
            if (outcome.Status == RelayTaskOutcomeStatus.Committed)
            {
                DrainCircuitBreaker.ClearHaltMarker(RootPath);
            }

            StatusText = outcome.Status == RelayTaskOutcomeStatus.Committed ? $"Committed {task.Id}" : $"Flagged {task.Id}";
            await ExportSummaryOnCompletion(task.Id, outcome);
            await LoadRunHistoryAsync(task.Id);
            if (PauseRequested)
            {
                StatusText = "Paused at task boundary";
            }
        }
        finally
        {
            ClearRunningTask(task.Id);
            // _runningTaskId is now cleared, so the detail-pane error can be
            // refreshed from the freshly-written status record: a flag surfaces
            // the new reason, a commit leaves it cleared. (The earlier
            // LoadRunHistoryAsync above runs while _runningTaskId == task.Id and
            // so deliberately leaves the error untouched.)
            RefreshSelectedTaskErrorAfterRun(task.Id);
            NotifyPauseStateChanged();
        }
    }

    /// <summary>The real single-task runner: the full pipeline over the stage agents.</summary>
    private RelayDriver CreateSingleRunDriver(RelayConfig config, IRelayEventSink sink, bool resume)
    {
        var subagentRunner = SubagentRunnerFactory.Create(config, sink, Env, VerboseSandboxDiagnostics);
        var dependencies = new RelayDriverDependencies(subagentRunner, CreateSandboxedTestRunner(config), sink, new GitInvoker());
        return new RelayDriver(dependencies, new RelayDriverOptions(CreateGitCommit: true, Resume: resume));
    }
}
