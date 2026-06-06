using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private async Task RunSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        if (!await EnsureRunnableAsync(SelectedTask.Id))
        {
            return;
        }

        var task = SelectedTask;
        await RunBusyAsync(async () =>
        {
            await RunOneAsync(task);
            await ReloadTaskListAsync(task.Id);
        });
    }

    [RelayCommand(CanExecute = nameof(CanDrain))]
    private async Task DrainQueueAsync()
    {
        if (PauseRequested)
        {
            StatusText = "Paused: no new task will start";
            return;
        }

        if (!await EnsureRunnableAsync(null))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var circuitBreaker = new DrainCircuitBreaker();
            DrainCircuitBreaker.ClearHaltMarker(RootPath);
            var queue = Tasks.Where(task => !task.NeedsReview).ToList();
            while (queue.FirstOrDefault() is { } task && !PauseRequested)
            {
                SelectedTask = task;
                var outcome = await RunOneAsync(task);
                queue.Remove(task);
                if (!ShowArchive)
                {
                    Tasks.Remove(task);
                }

                if (circuitBreaker.ShouldHalt(RootPath, outcome))
                {
                    StatusText = $"Drain halted: {circuitBreaker.HaltMessage ?? "task needs review"}";
                    await RefreshTasksAfterDrainAsync(outcome.TaskId);
                    return;
                }
            }

            StatusText = PauseRequested ? "Paused at task boundary" : "Queue drained";
            await RefreshTasksAfterDrainAsync();
        });
    }

    private bool CanFindTestCommand() => IsBackendReachable;

    [RelayCommand(CanExecute = nameof(CanFindTestCommand))]
    private async Task FindTestCommandAsync()
    {
        StatusText = "Asking the frontier model for the test command…";
        try
        {
            var command = await TestCommandFinder.FindAsync(RootPath);
            if (!string.IsNullOrWhiteSpace(command))
            {
                InitTestCommandInput = command.Trim();
                StatusText = "Detected a test command — review it, then Create config.";
            }
            else
            {
                StatusText = "The model didn't return a command — enter one manually.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't reach the model backend: {ex.Message}";
        }
    }

    private bool CanCreateConfig() => !IsBusy && !string.IsNullOrWhiteSpace(InitTestCommandInput);

    [RelayCommand(CanExecute = nameof(CanCreateConfig))]
    private async Task CreateConfigAsync()
    {
        RelayConfigWriter.Write(RootPath, InitTestCommandInput.Trim());
        await RefreshAsync();

        // If a Run was blocked by the missing config, resume it now that the config
        // loads. The pending id is cleared unconditionally so a non-resumable state
        // (archive view, task gone) can't leave it stale. NOTE: the actual resumed run
        // drives the real Swival pipeline (no runner injection seam), so it is verified
        // manually, not in unit tests.
        if (_pendingRunTaskId is { } pending)
        {
            _pendingRunTaskId = null;
            var resumed = ShowArchive ? null : Tasks.FirstOrDefault(task => task.Id == pending);
            if (resumed is not null)
            {
                SelectedTask = resumed;
                await RunSelectedCommand.ExecuteAsync(null);
            }
        }
    }

    private async Task<bool> EnsureRunnableAsync(string? pendingTaskId)
    {
        var result = await RelayConfigLoader.TryLoadAsync(RootPath);
        if (result.IsRunnable)
        {
            NeedsInitialization = false;
            ConfigDiagnostic = null;
            return true;
        }

        _pendingRunTaskId = pendingTaskId;
        NeedsInitialization = result.NeedsInitialization;
        ConfigDiagnostic = result.Status == RelayConfigStatus.Malformed ? result.Diagnostic : null;
        StatusText = result.Status == RelayConfigStatus.Malformed
            ? result.Diagnostic!
            : "No usable .relay/config.json — initialize this project to run.";
        return false;
    }

    private async Task<RelayTaskOutcome> RunOneAsync(TaskRowViewModel task)
    {
        ResetStages();
        ClearLogState();
        SelectedTaskError = null;
        StatusText = $"Running {task.Id}";
        BeginRunningTask(task);
        NotifyPauseStateChanged();
        var config = await RelayConfigLoader.LoadAsync(RootPath);
        var observable = new ObservableRelayEventSink(HandleRelayEvent);
        var fileSink = new FileRelayEventSink(Path.Combine(RootPath, ".relay", task.Id, "run.log"));
        var sink = new CompositeRelayEventSink(observable, fileSink);
        var dependencies = new RelayDriverDependencies(new SwivalSubagentRunner(config, eventSink: sink), new ShellTestRunner(), sink);
        var driver = new RelayDriver(dependencies, RelayDriverOptions.Default);
        try
        {
            var outcome = await driver.RunTaskAsync(RootPath, task.Id);
            StatusText = outcome.Status == RelayTaskOutcomeStatus.Committed ? $"Committed {task.Id}" : $"Flagged {task.Id}";
            await LoadRunHistoryAsync(task.Id);
            if (PauseRequested)
            {
                StatusText = "Paused at task boundary";
            }

            return outcome;
        }
        finally
        {
            ClearRunningTask(task.Id);
            NotifyPauseStateChanged();
        }
    }
}
