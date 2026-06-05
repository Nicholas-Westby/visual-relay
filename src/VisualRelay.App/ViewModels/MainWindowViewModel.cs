using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.App.Services;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly IBrush PauseIdleBackground = Brush.Parse("#2B2416");
    private static readonly IBrush PauseIdleBorder = Brush.Parse("#9E7A2D");
    private static readonly IBrush PauseIdleForeground = Brush.Parse("#F0CA66");
    private static readonly IBrush PauseActiveBackground = Brush.Parse("#3A2D12");
    private static readonly IBrush PauseActiveBorder = Brush.Parse("#F2C66D");
    private static readonly IBrush PauseActiveForeground = Brush.Parse("#FFE7A4");
    private static readonly IBrush BackendUpBrush = Brush.Parse("#5AD47D");
    private static readonly IBrush BackendDownBrush = Brush.Parse("#F36F63");

    private IFolderPicker _folderPicker;
    private readonly List<RelayEvent> _allTaskEvents = [];
    private readonly List<TraceEntry> _allTraceEntries = [];
    private readonly Dictionary<string, List<RelayEvent>> _liveEventsByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TraceEntry>> _liveTraceEntriesByTask = new(StringComparer.Ordinal);
    private int? _selectedStageFilter;
    private DispatcherTimer? _backendMonitor;
    private TaskRowViewModel? _runningTask;
    private string? _runningTaskId;
    private int? _runningStageNumber;
    private string? _runningStageName;

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

        Tasks.CollectionChanged += (_, _) =>
        {
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<StageRowViewModel> Stages { get; } = [];
    public ObservableCollection<RelayEvent> Events { get; } = [];
    public ObservableCollection<TraceEntry> TraceEntries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    [NotifyPropertyChangedFor(nameof(PauseNoticeText))]
    [NotifyPropertyChangedFor(nameof(IsPauseNoticeVisible))]
    [NotifyPropertyChangedFor(nameof(PauseButtonBackground))]
    [NotifyPropertyChangedFor(nameof(PauseButtonBorderBrush))]
    [NotifyPropertyChangedFor(nameof(PauseButtonForeground))]
    private bool _pauseRequested;

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
    [NotifyCanExecuteChangedFor(nameof(FollowRunningTaskCommand))]
    [NotifyPropertyChangedFor(nameof(IsViewingDifferentTaskDuringRun))]
    [NotifyPropertyChangedFor(nameof(ViewingRunContextText))]
    private TaskRowViewModel? _selectedTask;

    [ObservableProperty]
    private bool _needsInitialization;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateConfigCommand))]
    private string _initTestCommandInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigDiagnostic))]
    private string? _configDiagnostic;

    public bool HasConfigDiagnostic => ConfigDiagnostic is not null;

    // Set when a Run was blocked by a missing config so guided init can resume it.
    private string? _pendingRunTaskId;

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
    [NotifyPropertyChangedFor(nameof(HasSelectedTaskError))]
    private string? _selectedTaskError;

    public bool HasSelectedTaskError => !string.IsNullOrEmpty(SelectedTaskError);

    [ObservableProperty]
    private string _logScopeLabel = "full";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyPropertyChangedFor(nameof(PauseNoticeText))]
    private bool _isBusy;

    // Backend reachability surfaced to the UI. Defaults to reachable so the
    // startup banner stays hidden until a probe says otherwise. The later
    // top-bar status task reuses this state + RefreshBackendStatusAsync rather
    // than running a second probe.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackendStatusBrush))]
    [NotifyPropertyChangedFor(nameof(BackendStatusLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartBackendCommand))]
    private bool _isBackendReachable = true;

    [ObservableProperty]
    private string? _backendStatusMessage;

    public void UseFolderPicker(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
    }

    public async Task LoadInitialAsync()
    {
        // RefreshAsync now also probes the backend, so probe directly only when
        // there is no root to refresh. Non-blocking either way: the probe runs
        // off the UI thread (HttpClient async) and the window is already shown,
        // so a down backend never freezes startup.
        if (Directory.Exists(RootPath))
        {
            await RefreshAsync();
        }
        else
        {
            await RefreshBackendStatusAsync();
        }
    }

    // Reusable seam: probes the model backend once and updates the VM state.
    // Safe to call from the UI thread (the probe never throws) and reusable by
    // the later persistent top-bar indicator.
    public async Task RefreshBackendStatusAsync()
    {
        var readiness = await BackendReadinessProbe.CheckAsync();
        IsBackendReachable = readiness.IsReady;
        BackendStatusMessage = readiness.Message;
    }

    // Starts a light-interval poll that keeps the top-bar status dot honest
    // without blocking. Called ONLY from App startup (never the ctor or
    // LoadInitialAsync) so unit tests spin no timer.
    public void StartBackendMonitoring()
    {
        _backendMonitor?.Stop();
        _backendMonitor = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _backendMonitor.Tick += (_, _) => _ = RefreshBackendStatusAsync();
        _backendMonitor.Start();
    }

    public string RootName => RootFolderDisplay.Name(RootPath);
    public string RootParentPath => RootFolderDisplay.Parent(RootPath);
    public string WindowTitle => $"Visual Relay - {RootName}";
    public string TaskListTitle => ShowArchive ? "ARCHIVE" : "QUEUE";
    public string TaskListToggleText => ShowArchive ? "Queue" : "Archive";
    public string PauseButtonText => PauseRequested ? "Resume" : "Pause after task";
    public string PauseNoticeText => PauseRequested
        ? IsBusy ? $"Stops after {_runningTaskId ?? "current task"}" : "Paused before next task"
        : string.Empty;
    public bool IsPauseNoticeVisible => PauseRequested;
    public IBrush BackendStatusBrush => IsBackendReachable ? BackendUpBrush : BackendDownBrush;
    public string BackendStatusLabel => IsBackendReachable
        ? $"backend: {new Uri(ModelBackend.BaseUrl).Authority}"
        : "backend down";
    public IBrush PauseButtonBackground => PauseRequested ? PauseActiveBackground : PauseIdleBackground;
    public IBrush PauseButtonBorderBrush => PauseRequested ? PauseActiveBorder : PauseIdleBorder;
    public IBrush PauseButtonForeground => PauseRequested ? PauseActiveForeground : PauseIdleForeground;
    public bool IsViewingDifferentTaskDuringRun =>
        _runningTaskId is not null && SelectedTask is not null && !string.Equals(SelectedTask.Id, _runningTaskId, StringComparison.Ordinal);
    public string ViewingRunContextText => IsViewingDifferentTaskDuringRun ? $"Viewing {SelectedTask!.Id} · running {_runningTaskId}" : string.Empty;

}
