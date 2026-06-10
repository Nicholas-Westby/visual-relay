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
            Func<string, IRelayEventSink> planSinkFactory = _ =>
                new ObservableRelayEventSink(HandleRelayEvent);

            var executeSink = new ObservableRelayEventSink(HandleRelayEvent);
            var executeTestRunner = new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds));

            Func<string, ISubagentRunner> planSubagentFactory = taskId =>
                new SwivalSubagentRunner(config, eventSink: new ObservableRelayEventSink(HandleRelayEvent));
            var planTestRunner = new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds));

            var lifecycle = CreateDrainLifecycleCallbacks();

            var controller = new RelayQueueController(
                RootPath,
                new GuiTaskRunner(RootPath, config, executeSink, executeTestRunner),
                planSubagentRunnerFactory: planSubagentFactory,
                planTestRunner: planTestRunner,
                planEventSinkFactory: planSinkFactory,
                lifecycle: lifecycle);

            await controller.RefreshAsync();
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
        var hookResult = await HookInstaller.InstallAsync(RootPath, CancellationToken.None);
        if (!hookResult.Installed && hookResult.Warning is not null)
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
        ResetStages(task.Id);
        ClearLogState();
        SelectedTaskError = null;
        StatusText = $"Running {task.Id}";
        BeginRunningTask(task);
        NotifyPauseStateChanged();
        var config = await RelayConfigLoader.LoadAsync(RootPath);
        var observable = new ObservableRelayEventSink(HandleRelayEvent);
        var fileSink = new FileRelayEventSink(Path.Combine(RootPath, ".relay", task.Id, "run.log"));
        var sink = new CompositeRelayEventSink(observable, fileSink);
        var subagentRunner = new SwivalSubagentRunner(config, eventSink: sink);
        var dependencies = new RelayDriverDependencies(subagentRunner, new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), sink);
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

    private DrainLifecycleCallbacks CreateDrainLifecycleCallbacks()
    {
        return new DrainLifecycleCallbacks
        {
            OnPlanningStarted = taskId =>
            {
                StatusText = $"Planning {taskId}…";
                Tasks.FirstOrDefault(t => t.Id == taskId)?.MarkPlanning();
            },
            OnPlanningCompleted = (taskId, status) =>
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    if (status == RelayTaskOutcomeStatus.Flagged)
                        task.MarkIdle();
                    else
                        task.MarkPlanned();
                }
            },
            OnExecuteStarted = taskId =>
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                    BeginRunningTask(task);
            },
            OnExecuteCompleted = (taskId, _) => ClearRunningTask(taskId)
        };
    }

    /// <summary>
    /// Thin <see cref="IRelayTaskRunner"/> that creates a fresh driver per
    /// execute call. Each call gets its own <see cref="SwivalSubagentRunner"/>
    /// wired to a <see cref="CompositeRelayEventSink"/> so both driver and
    /// subagent trace events land in run.log.
    /// </summary>
    private sealed class GuiTaskRunner : IRelayTaskRunner
    {
        private readonly string _mainRootPath;
        private readonly RelayConfig _config;
        private readonly IRelayEventSink _sharedSink;
        private readonly ITestRunner _testRunner;

        public GuiTaskRunner(string mainRootPath, RelayConfig config,
            IRelayEventSink sharedSink, ITestRunner testRunner)
        {
            _mainRootPath = mainRootPath;
            _config = config;
            _sharedSink = sharedSink;
            _testRunner = testRunner;
        }

        public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            var fileSink = new FileRelayEventSink(Path.Combine(_mainRootPath, ".relay", taskId, "run.log"));
            var sink = new CompositeRelayEventSink(_sharedSink, fileSink);
            var subagentRunner = new SwivalSubagentRunner(_config, eventSink: sink);
            var deps = new RelayDriverDependencies(subagentRunner, _testRunner, sink);
            var driver = new RelayDriver(deps, new RelayDriverOptions(CreateGitCommit: true, Resume: true));
            return driver.RunTaskAsync(rootPath, taskId, cancellationToken);
        }
    }
}
