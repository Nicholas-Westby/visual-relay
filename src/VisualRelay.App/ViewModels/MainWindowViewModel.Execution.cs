using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
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

    private async Task<RelayTaskOutcome> RunOneAsync(TaskRowViewModel task)
    {
        ResetStages();
        ClearLogState();
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
