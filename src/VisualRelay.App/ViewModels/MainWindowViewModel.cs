using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Core.Tasks;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private IFolderPicker _folderPicker;
    private bool _pauseRequested;

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

    public ObservableCollection<RelayTaskItem> Tasks { get; } = [];
    public ObservableCollection<StageRowViewModel> Stages { get; } = [];
    public ObservableCollection<RelayEvent> Events { get; } = [];
    public ObservableCollection<TraceEntry> TraceEntries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyPropertyChangedFor(nameof(RootName))]
    [NotifyPropertyChangedFor(nameof(RootParentPath))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _rootPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private RelayTaskItem? _selectedTask;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _selectedTaskMarkdown = string.Empty;

    [ObservableProperty]
    private string _selectedTaskContext = string.Empty;

    [ObservableProperty]
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
            Tasks.Clear();
            var repository = new RelayTaskRepository(RootPath);
            foreach (var task in await repository.ListAsync())
            {
                Tasks.Add(task);
            }

            SelectedTask = Tasks.FirstOrDefault();
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

        var taskId = SelectedTask.Id;
        await RunOneAsync(SelectedTask);
        await RefreshAsync();
        SelectedTask = Tasks.FirstOrDefault(task => task.Id == taskId) ?? Tasks.FirstOrDefault();
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

    partial void OnSelectedTaskChanged(RelayTaskItem? value)
    {
        _ = LoadSelectedTaskAsync(value);
    }

    private async Task<RelayTaskOutcome> RunOneAsync(RelayTaskItem task)
    {
        ResetStages();
        Events.Clear();
        TraceEntries.Clear();
        StatusText = $"Running {task.Id}";
        var config = await RelayConfigLoader.LoadAsync(RootPath);
        var sink = new ObservableRelayEventSink(HandleRelayEvent);
        var dependencies = new RelayDriverDependencies(new SwivalSubagentRunner(config, eventSink: sink), new ShellTestRunner(), sink);
        var driver = new RelayDriver(dependencies, RelayDriverOptions.Default);
        var outcome = await driver.RunTaskAsync(RootPath, task.Id);
        StatusText = outcome.Status == RelayTaskOutcomeStatus.Committed ? $"Committed {task.Id}" : $"Flagged {task.Id}";
        await LoadTraceAsync(task.Id);
        return outcome;
    }

    private async Task LoadSelectedTaskAsync(RelayTaskItem? task)
    {
        if (task is null)
        {
            SelectedTaskMarkdown = string.Empty;
            SelectedTaskContext = string.Empty;
            TraceEntries.Clear();
            return;
        }

        var input = await new RelayTaskRepository(RootPath).ReadTaskInputAsync(task);
        SelectedTaskMarkdown = input.Markdown;
        SelectedTaskContext = input.Context ?? string.Empty;
        await LoadTraceAsync(task.Id);
    }

    private async Task LoadTraceAsync(string taskId)
    {
        TraceEntries.Clear();
        var file = RelayTraceLocator.FindLatestTraceFile(RootPath, taskId);
        if (file is null)
        {
            return;
        }

        var text = await File.ReadAllTextAsync(file);
        foreach (var entry in RelayTraceParser.Parse(text))
        {
            TraceEntries.Add(entry);
        }
    }

}
