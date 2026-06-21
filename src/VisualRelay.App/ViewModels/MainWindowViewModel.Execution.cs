using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Queue;
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
            var config = await RelayConfigLoader.LoadAsync(RootPath);

            // Per-task event sink factory for planning: each planning task
            // gets its own ObservableRelayEventSink wired to HandleRelayEvent.
            IRelayEventSink PlanSinkFactory(string _) =>
                new ObservableRelayEventSink(HandleRelayEvent);

            var executeSink = new ObservableRelayEventSink(HandleRelayEvent);
            var executeTestRunner = new SandboxedTestRunner(
                new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), config);

            ISubagentRunner PlanSubagentFactory(string _) =>
                new SwivalSubagentRunner(config, eventSink: new ObservableRelayEventSink(HandleRelayEvent));
            var planTestRunner = new SandboxedTestRunner(
                new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), config);

            var lifecycle = CreateDrainLifecycleCallbacks();

            var controller = new RelayQueueController(
                RootPath,
                new GuiTaskRunner(RootPath, config, executeSink, executeTestRunner),
                planSubagentRunnerFactory: PlanSubagentFactory,
                planTestRunner: planTestRunner,
                planEventSinkFactory: PlanSinkFactory,
                lifecycle: lifecycle);

            await controller.RefreshAsync();
            // RefreshAsync already seeds from the persisted manual order (the shared
            // source of truth). Re-applying the app's visible order keeps the drain
            // aligned with any in-session reorder not yet reloaded — idempotent when
            // they already match.
            controller.ApplyOrder(Tasks.Select(t => t.Id).ToList());
            // Wire pause.
            if (PauseRequested)
                controller.RequestPause();

            var results = await controller.DrainAsync();

            var flaggedCount = results.Count(r => r.Status == RelayTaskOutcomeStatus.Flagged);
            var committedCount = results.Count(r => r.Status == RelayTaskOutcomeStatus.Committed);
            var plannedCount = results.Count(r => r.Status == RelayTaskOutcomeStatus.Planned);

            if (controller.State == RelayQueueState.Paused)
                StatusText = "Paused at task boundary";
            else if (controller.State == RelayQueueState.Failed)
                StatusText = "Drain halted: commit gate rejected consecutive tasks";
            else if (controller.State == RelayQueueState.ReviewNeeded)
                StatusText = flaggedCount > 0
                    ? $"Queue drained · {flaggedCount} flagged for review"
                    : "Queue drained";
            else
                StatusText = committedCount > 0
                    ? $"Queue drained · {committedCount} committed"
                    : plannedCount > 0
                        ? $"Queue drained · {plannedCount} planned"
                        : "Queue drained";

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
        var command = InitTestCommandInput.Trim();

        // Smoke-validate before writing — never persist a command that can't start.
        var runner = new DirectExecTestRunner(TimeSpan.FromSeconds(5));
        var validator = new TestCommandValidator(runner);
        var validation = await validator.ValidateAsync(RootPath, command);

        if (!validation.Accepted)
        {
            StatusText = validation.RejectionReason ?? "test command validation failed";
            return;
        }

        RelayConfigWriter.Write(RootPath, command);
        var gitInvoker = new GitInvoker();
        var hookResult = await HookInstaller.InstallAsync(RootPath, CancellationToken.None, gitInvoker);
        if (hookResult is { Installed: false, Warning: not null })
        {
            StatusText = hookResult.Warning;
        }

        await RefreshAsync();

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

    // internal (not private) so a VM test can drive the gate directly without
    // launching a run; the App's commands call it the same way.
    internal async Task<bool> EnsureRunnableAsync(string? pendingTaskId)
    {
        // Greenfield: when the test command is still the placeholder and the project
        // has since gained a recognizable toolchain (a scaffold task ran), adopt the
        // real test command before gating. Best-effort: a no-op for normal repos, and
        // a failure here must never block an otherwise-runnable task.
        try
        {
            await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(RootPath);
        }
        catch
        {
            // Detection/validation hiccup — fall through to gating on the current config.
        }

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

        if (!IsHuggingFaceConfigured)
        {
            _pendingHfRunTaskId = pendingTaskId;
            StatusText = HfGateMessage;
            return false;
        }

        // Fail fast before launching when a required tool (swival always; nono when
        // the sandbox is on) isn't on PATH — the user gets an actionable message up
        // front, not a failed stage full of nono advisory noise. Reuse the runner's
        // MissingToolsMessage verbatim so both surfaces never drift. PATH comes from
        // the injected accessor when present (tests), else the real process PATH.
        var missingTools = SwivalSubagentRunner.MissingRequiredTools(
            result.Config, EnvironmentAccessor?.GetEnvironmentVariable("PATH"));
        if (missingTools.Count > 0)
        {
            StatusText = SwivalSubagentRunner.MissingToolsMessage(missingTools);
            return false;
        }

        NeedsInitialization = false;
        ConfigDiagnostic = null;
        return true;
    }

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
        var subagentRunner = new SwivalSubagentRunner(config, eventSink: sink);
        var dependencies = new RelayDriverDependencies(subagentRunner, new SandboxedTestRunner(new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), config), sink, new GitInvoker());
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
