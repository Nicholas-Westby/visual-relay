using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private IFolderPicker _folderPicker;
    private bool _pauseRequested;
    private readonly List<RelayEvent> _allTaskEvents = [];
    private readonly List<TraceEntry> _allTraceEntries = [];
    private readonly Dictionary<string, List<RelayEvent>> _liveEventsByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TraceEntry>> _liveTraceEntriesByTask = new(StringComparer.Ordinal);
    private int? _selectedStageFilter;
    private TaskRowViewModel? _runningTask;

    public MainWindowViewModel()
        : this(new NullFolderPicker())
    {
    }

    public MainWindowViewModel(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
        _rootPath = RootFolderDisplay.DefaultPath();
        foreach (var stage in RelayStages.All)
        {
            Stages.Add(new StageRowViewModel(stage));
        }
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<StageRowViewModel> Stages { get; } = [];
    public ObservableCollection<RelayEvent> Events { get; } = [];
    public ObservableCollection<TraceEntry> TraceEntries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyPropertyChangedFor(nameof(RootName))]
    [NotifyPropertyChangedFor(nameof(RootParentPath))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _rootPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private TaskRowViewModel? _selectedTask;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _selectedTaskMarkdown = string.Empty;

    [ObservableProperty]
    private string _selectedTaskContext = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyPropertyChangedFor(nameof(TaskListTitle))]
    [NotifyPropertyChangedFor(nameof(TaskListToggleText))]
    private bool _showArchive;

    [ObservableProperty]
    private string _selectedTaskMetricLabel = "No run history";

    [ObservableProperty]
    private string _logScopeLabel = "full";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    private bool _isBusy;

    public void UseFolderPicker(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
    }

    public Task LoadInitialAsync() =>
        Directory.Exists(RootPath) ? RefreshAsync() : Task.CompletedTask;

    public string RootName => RootFolderDisplay.Name(RootPath);
    public string RootParentPath => RootFolderDisplay.Parent(RootPath);
    public string WindowTitle => $"Visual Relay - {RootName}";
    public string TaskListTitle => ShowArchive ? "ARCHIVE" : "QUEUE";
    public string TaskListToggleText => ShowArchive ? "Queue" : "Archive";

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var selected = await _folderPicker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        RootPath = selected;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusText = "Refreshing";
            await ReloadTaskListAsync();
            StatusText = FormatQueueStatus();
        });
    }

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
        _pauseRequested = false;
        await RunBusyAsync(async () =>
        {
            var circuitBreaker = new DrainCircuitBreaker();
            while (Tasks.FirstOrDefault(task => !task.NeedsReview) is { } task && !_pauseRequested)
            {
                SelectedTask = task;
                var outcome = await RunOneAsync(task);
                Tasks.Remove(task);
                if (circuitBreaker.ShouldHalt(RootPath, outcome))
                {
                    StatusText = "Drain halted: commit gate rejected consecutive tasks";
                    await RefreshTasksAfterDrainAsync();
                    return;
                }
            }

            StatusText = _pauseRequested ? "Paused" : "Queue drained";
            await RefreshTasksAfterDrainAsync();
        });
    }

    [RelayCommand]
    private void Pause()
    {
        _pauseRequested = true;
        StatusText = IsBusy ? "Pause requested" : "Paused";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index > 0)
        {
            Tasks.Move(index, index - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ToggleArchiveAsync()
    {
        ShowArchive = !ShowArchive;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index >= 0 && index < Tasks.Count - 1)
        {
            Tasks.Move(index, index + 1);
        }
    }

    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        _selectedStageFilter = null;
        LogScopeLabel = "full";
        foreach (var task in Tasks)
        {
            task.IsSelected = ReferenceEquals(task, value);
        }

        _ = LoadSelectedTaskAsync(value);
    }

    private async Task<RelayTaskOutcome> RunOneAsync(TaskRowViewModel task)
    {
        ResetStages();
        ClearLogState();
        StatusText = $"Running {task.Id}";
        _runningTask = task;
        task.MarkRunning();
        var config = await RelayConfigLoader.LoadAsync(RootPath);
        var sink = new ObservableRelayEventSink(HandleRelayEvent);
        var dependencies = new RelayDriverDependencies(new SwivalSubagentRunner(config, eventSink: sink), new ShellTestRunner(), sink);
        var driver = new RelayDriver(dependencies, RelayDriverOptions.Default);
        try
        {
            var outcome = await driver.RunTaskAsync(RootPath, task.Id);
            StatusText = outcome.Status == RelayTaskOutcomeStatus.Committed ? $"Committed {task.Id}" : $"Flagged {task.Id}";
            await LoadRunHistoryAsync(task.Id);
            return outcome;
        }
        finally
        {
            task.MarkIdle();
            _runningTask = null;
        }
    }

    private async Task LoadSelectedTaskAsync(TaskRowViewModel? task)
    {
        if (task is null)
        {
            SelectedTaskMarkdown = string.Empty;
            SelectedTaskContext = string.Empty;
            SelectedTaskMetricLabel = "No run history";
            ClearLogState();
            ResetStages();
            return;
        }

        ResetStages();
        var input = await new RelayTaskRepository(RootPath).ReadTaskInputAsync(task.Task);
        SelectedTaskMarkdown = input.Markdown;
        SelectedTaskContext = input.Context ?? string.Empty;
        await LoadRunHistoryAsync(task.Id);
    }

    [RelayCommand]
    private void SelectStage(StageRowViewModel stage)
    {
        if (_selectedStageFilter == stage.Number)
        {
            _selectedStageFilter = null;
            stage.IsSelected = false;
            LogScopeLabel = "full";
            ApplyLogFilter();
            return;
        }

        _selectedStageFilter = stage.Number;
        foreach (var item in Stages)
        {
            item.IsSelected = item.Number == stage.Number;
        }

        LogScopeLabel = $"stage {stage.Number:00}";
        ApplyLogFilter();
    }
}
