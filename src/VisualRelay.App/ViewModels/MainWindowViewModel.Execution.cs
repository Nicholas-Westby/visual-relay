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
    private RelayQueueController? _activeDrainController;

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

        await RunSelectedTaskAsync(SelectedTask);
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

        await RunSelectedTaskAsync(SelectedTask, resume: true);
    }

    /// <summary>
    /// The cancellable single-task run behind both Run and Resume: one scope, one
    /// token, threaded into the driver so a cancel reaches the running stage.
    /// Internal so a test can drive it without re-running the pre-run gate.
    /// </summary>
    internal Task RunSelectedTaskAsync(TaskRowViewModel task, bool resume = false) =>
        RunCancellableAsync(async cancellationToken =>
        {
            await RunOneAsync(task, cancellationToken, resume);
            await ReloadTaskListAsync(task.Id);
        });

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

        await RunCancellableAsync(async cancellationToken =>
        {
            var config = await RelayConfigLoader.LoadAsync(RootPath);

            // Per-task event sink factory for planning: each planning task
            // gets its own ObservableRelayEventSink wired to HandleRelayEvent.
            IRelayEventSink PlanSinkFactory(string _) => new ObservableRelayEventSink(HandleRelayEvent);

            var executeSink = new ObservableRelayEventSink(HandleRelayEvent);
            var executeTestRunner = CreateSandboxedTestRunner(config);

            // Built from the planning driver's own sink so the agent's traces reach the
            // task's run.log, not just the GUI.
            ISubagentRunner PlanSubagentFactory(string _, IRelayEventSink sink) =>
                CreateSubagentRunner(config, sink);
            var planTestRunner = CreateSandboxedTestRunner(config);

            var lifecycle = CreateDrainLifecycleCallbacks();

            var controller = new RelayQueueController(
                RootPath,
                new GuiTaskRunner(RootPath, config, executeSink, executeTestRunner, VerboseSandboxDiagnostics),
                planSubagentRunnerFactory: PlanSubagentFactory,
                planTestRunner: planTestRunner,
                planEventSinkFactory: PlanSinkFactory,
                lifecycle: lifecycle);

            _activeDrainController = controller;

            // RestartBetweenTasks: when the drain stops after a committed
            // task the controller invokes this callback with the handoff.
            // We capture it so we can spawn the relauncher after the drain
            // returns and the controller is done writing the sidecar.
            RestartHandoff? pendingRestartHandoff = null;
            controller.OnRestartRequested = h => pendingRestartHandoff = h;

            // Bridge: when a new task is created mid-drain (CreateNewTaskAsync →
            // ReloadTaskListAsync), it lands in the GUI's Tasks collection but not in
            // controller.Tasks. This source lets the drain loop pull fresh task items
            // from the GUI at each checkpoint, so CollectNewTasks can discover them.
            controller.SetExternalTaskSource(() => Tasks.Select(t => t.Task).ToList());

            await controller.RefreshAsync();
            // RefreshAsync already seeds from the persisted manual order (the shared
            // source of truth). Re-applying the app's visible order keeps the drain
            // aligned with any in-session reorder not yet reloaded — idempotent when
            // they already match.
            controller.ApplyOrder(Tasks.Select(t => t.Id).ToList());
            // Remove any task that is currently being rewritten — its on-disk spec is
            // not finalised until the rewrite completes, so it must not be executed.
            for (var i = controller.Tasks.Count - 1; i >= 0; i--)
                if (_rewritingTaskIds.Contains(controller.Tasks[i].Id))
                    controller.Tasks.RemoveAt(i);

            IReadOnlyList<RelayTaskOutcome> results;
            try { results = await controller.DrainAsync(cancellationToken, SelectedRunAllMode); }
            finally { _activeDrainController = null; }

            var flaggedCount = results.Count(r => r.Status == RelayTaskOutcomeStatus.Flagged);
            var committedCount = results.Count(r => r.Status == RelayTaskOutcomeStatus.Committed);
            var plannedCount = results.Count(r => r.Status == RelayTaskOutcomeStatus.Planned);

            if (controller.State == RelayQueueState.Cancelled)
                StatusText = "Run cancelled";
            else if (controller.State == RelayQueueState.Paused)
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

            DropStaleRunAnchorsAfterDrain(); // drop anchors for tasks left Planned (resume re-anchors)
            await RefreshTasksAfterDrainAsync();

            // RestartBetweenTasks: if the drain stopped because of a
            // committed task, the handoff sidecar is already on disk.
            // Now spawn the detached relauncher and shut down so the
            // next instance can recompile and resume.
            if (pendingRestartHandoff is not null)
            {
                await TriggerRestartAndShutdownAsync(pendingRestartHandoff);
            }
        });
    }

    [RelayCommand]
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
        StatusText = "Validating test command (may compile up to 2 min)…";
        var runner = InitValidationRunnerFactory?.Invoke(ProjectBootstrapper.CreateConfigValidationTimeout)
            ?? ProjectBootstrapper.CreateValidationRunner(ProjectBootstrapper.CreateConfigValidationTimeout);
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

    /// <summary>
    /// Test seam: installs a controller as the active drain controller so the
    /// VM-level pause test can verify the full UI→controller round-trip.
    /// Production sets <see cref="_activeDrainController"/> in
    /// <see cref="DrainQueueAsync"/>; only tests call this setter.
    /// </summary>
    internal void SetActiveDrainControllerForTests(RelayQueueController? c) => _activeDrainController = c;

}
