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

    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private async Task ResumeSelectedAsync()
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
            await RunOneAsync(task, resume: true);
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
            var flaggedCount = 0;
            while (queue.FirstOrDefault() is { } task && !PauseRequested)
            {
                SelectedTask = task;
                var outcome = await RunOneAsync(task);
                queue.Remove(task);

                if (circuitBreaker.ShouldHalt(RootPath, outcome))
                {
                    StatusText = $"Drain halted: {circuitBreaker.HaltMessage ?? "task needs review"}";
                    await RefreshTasksAfterDrainAsync(outcome.TaskId);
                    return;
                }

                if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
                {
                    flaggedCount++;
                    // Keep the flagged task visible in the list as NeedsReview.
                    if (!ShowArchive)
                    {
                        Tasks.Remove(task);
                        Tasks.Add(new TaskRowViewModel(task.Task with { ReviewReason = outcome.Reason ?? "Needs review" }));
                    }
                }
                else if (!ShowArchive)
                {
                    Tasks.Remove(task);
                }
            }

            StatusText = flaggedCount > 0
                ? $"Queue drained · {flaggedCount} flagged for review"
                : PauseRequested ? "Paused at task boundary" : "Queue drained";
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
        var hookResult = await HookInstaller.InstallAsync(RootPath, CancellationToken.None);
        if (!hookResult.Installed && hookResult.Warning is not null)
        {
            StatusText = hookResult.Warning;
        }

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
        if (!result.IsRunnable)
        {
            _pendingRunTaskId = pendingTaskId;
            NeedsInitialization = result.NeedsInitialization;
            ConfigDiagnostic = result.Status == RelayConfigStatus.Malformed ? result.Diagnostic : null;
            StatusText = result.Status == RelayConfigStatus.Malformed
                ? result.Diagnostic!
                : "No usable .relay/config.json — initialize this project to run.";
            return false;
        }

        // Config is present — now gate on HF_TOKEN, the floor under every tier.
        if (!IsHuggingFaceConfigured)
        {
            _pendingHfRunTaskId = pendingTaskId;
            StatusText = HfGateMessage;
            return false;
        }

        NeedsInitialization = false;
        ConfigDiagnostic = null;
        return true;
    }

    private async Task<RelayTaskOutcome> RunOneAsync(TaskRowViewModel task, bool resume = false)
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
        var dependencies = new RelayDriverDependencies(new SwivalSubagentRunner(config, eventSink: sink), new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), sink);
        var driver = new RelayDriver(dependencies, new RelayDriverOptions(CreateGitCommit: true, Resume: resume));
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
