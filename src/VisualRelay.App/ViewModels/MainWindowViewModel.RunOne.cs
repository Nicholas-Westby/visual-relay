using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

// ReSharper disable once UnusedType.Global — partial of MainWindowViewModel
public partial class MainWindowViewModel
{
    private async Task RunOneAsync(TaskRowViewModel task, bool resume = false)
    {
        ResetStages(task.Id);
        ClearLogState();
        StatusText = $"Running {task.Id}";
        // BeginRunningTask clears the stale detail-pane error when this is the
        // selected task (run start ⇒ the prior run's error no longer applies).
        BeginRunningTask(task);
        NotifyPauseStateChanged();
        var config = await RelayConfigLoader.LoadAsync(RootPath);
        var observable = new ObservableRelayEventSink(HandleRelayEvent);
        var fileSink = new FileRelayEventSink(Path.Combine(RootPath, ".relay", task.Id, "run.log"));
        var sink = new CompositeRelayEventSink(observable, fileSink);
        var subagentRunner = new SwivalSubagentRunner(config, eventSink: sink, verboseDiagnostics: VerboseSandboxDiagnostics);
        var dependencies = new RelayDriverDependencies(subagentRunner, new SandboxedTestRunner(new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), config, VerboseSandboxDiagnostics), sink, new GitInvoker());
        var driver = new RelayDriver(dependencies, new RelayDriverOptions(CreateGitCommit: true, Resume: resume));
        try
        {
            var outcome = await driver.RunTaskAsync(RootPath, task.Id);
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
}
