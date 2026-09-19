using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>Counts reloads started, so one that a newer reload overtook applies nothing.</summary>
    private int _taskListGeneration;

    /// <summary>
    /// Reloads the config-derived state and the task list, then selects
    /// <paramref name="preferredTaskId"/>, the current task, or the first.
    /// <para>
    /// Reloads overlap: a refresh during a run, a created task after an archive toggle, a
    /// file-watch refresh during an edit. This used to clear the list before awaiting the
    /// listing, so two overlapping reloads cleared it twice and then both filled it, and
    /// every task was listed twice until the next reload (seen on Windows as a created task
    /// found twice). Now only the newest reload applies anything past each await, and the
    /// list is cleared and refilled with no await in between.
    /// </para>
    /// </summary>
    private async Task ReloadTaskListAsync(string? preferredTaskId = null)
    {
        var generation = Interlocked.Increment(ref _taskListGeneration);
        var configResult = await RelayConfigLoader.TryLoadAsync(RootPath);
        if (generation != Volatile.Read(ref _taskListGeneration))
            return;

        NeedsInitialization = !ShowArchive && configResult.NeedsInitialization;
        ConfigDiagnostic = configResult.Status == RelayConfigStatus.Malformed ? configResult.Diagnostic : null;
        TestCommandIsPlaceholder = ProjectBootstrapper.IsPlaceholder(configResult.Config.TestCommand);
        if (configResult.Status == RelayConfigStatus.Loaded)
        {
            _isHydrating = true;
            StageTimeoutMinutes = Math.Clamp((int)Math.Round(configResult.Config.SubagentTimeoutMilliseconds / 60_000.0), 1, 720);
            TestTimeoutMinutes = Math.Clamp((int)Math.Round(configResult.Config.TestTimeoutMilliseconds / 60_000.0), 1, 720);
            _isHydrating = false;
            HydrateTurnBudget(configResult.Config);
            HydrateSkipTests(configResult.Config);
        }

        // IsNullOrEmpty (not WhiteSpace) so detection runs only when the user hasn't
        // touched the box yet; never clobbers a value the user has typed.
        if (NeedsInitialization && string.IsNullOrEmpty(InitTestCommandInput))
        {
            InitTestCommandInput = TestCommandDetector.Detect(RootPath);
        }

        var repository = new RelayTaskRepository(RootPath, new GitInvoker());
        // The archive is sorted by completion time and is not reorderable; only the
        // pending queue honors the user's persisted manual order (alphabetical
        // fallback for tasks without a saved rank — e.g. newly-created ones).
        var tasks = ShowArchive
            ? await repository.ListCompletedAsync()
            : new TaskOrderStore(RootPath).Apply(await repository.ListAsync(), task => task.Id);
        if (generation != Volatile.Read(ref _taskListGeneration))
            return;

        Tasks.Clear();
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        for (var i = 0; i < tasks.Count; i++)
        {
            var row = new TaskRowViewModel(tasks[i]);
            if (ShowArchive)
                row.DayHeader = ArchiveDayGrouping.HeadingFor(tasks, i, today) ?? string.Empty;
            Tasks.Add(row);
        }

        ApplyRunningTaskToRows();
        SelectedTask = preferredTaskId is null
            ? (SelectedTask is not null ? Tasks.FirstOrDefault(task => task.Id == SelectedTask.Id) : null) ?? Tasks.FirstOrDefault()
            : Tasks.FirstOrDefault(task => task.Id == preferredTaskId) ?? Tasks.FirstOrDefault();
        DrainQueueCommand.NotifyCanExecuteChanged();
    }
}
