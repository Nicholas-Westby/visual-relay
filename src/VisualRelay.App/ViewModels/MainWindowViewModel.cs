using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
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
    private IFilePicker _filePicker;
    private readonly List<RelayEvent> _allTaskEvents = [];
    private readonly List<TraceEntry> _allTraceEntries = [];
    private readonly Dictionary<string, List<RelayEvent>> _liveEventsByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TraceEntry>> _liveTraceEntriesByTask = new(StringComparer.Ordinal);
    private int? _selectedStageFilter;
    private DispatcherTimer? _backendMonitor;
    private DispatcherTimer? _elapsedTimer;
    // Multi-task running state: _runningTaskIds tracks every concurrently-running
    // task; _runningTaskId is the "followed" task in the detail pane.
    private readonly HashSet<string> _runningTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _runningStageNumbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _runningStageNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _runStartedAt = new(StringComparer.Ordinal);
    private string? _runningTaskId;

    // Rewrite state: separate from run state so rewrites don't block other tasks.
    private readonly HashSet<string> _rewritingTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _rewriteStartedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _rewriteCts = new(StringComparer.Ordinal);
    // Undo for "Rewrite with AI": snapshots the WHOLE task folder so a revert
    // restores attachments too, not just the spec .md. (Rewrite-specific members
    // live in the MainWindowViewModel.Rewrite partial.)
    private readonly RewriteUndoStore _rewriteUndo = new();

    public MainWindowViewModel(IEnvironmentAccessor? environmentAccessor = null)
        : this(new NullFolderPicker(), new NullFilePicker(), environmentAccessor)
    {
    }

    private MainWindowViewModel(IFolderPicker folderPicker, IFilePicker filePicker, IEnvironmentAccessor? environmentAccessor = null)
    {
        EnvironmentAccessor = environmentAccessor;
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _rootPath = RootFolderDisplay.DefaultPath();

        var uiState = UiStateStore.Load(EnvironmentAccessor);
        // Clamp a stale or absurd persisted width so a corrupt ui-state.json
        // can never break layout on startup (mirrors the splitter-drag clamp).
        _activityColumnWidth = Math.Clamp(
            uiState.ActivityColumnWidth, MinActivityColumnWidth, MaxActivityColumnWidth);
        _activityTabIndex = uiState.ActivityTabIndex;

        foreach (var stage in RelayStages.All)
        {
            Stages.Add(new StageRowViewModel(stage, SelectStageCommand));
        }
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<StageRowViewModel> Stages { get; } = [];
    public ObservableCollection<RelayEvent> Events { get; } = [];
    public ObservableCollection<TraceEntry> TraceEntries { get; } = [];
    public ObservableCollection<AttachmentRowViewModel> Attachments { get; } = [];

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
    [NotifyCanExecuteChangedFor(nameof(FollowRunningTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]
    [NotifyPropertyChangedFor(nameof(TaskListTitle))]
    [NotifyPropertyChangedFor(nameof(TaskListToggleText))]
    private bool _showArchive;

    [ObservableProperty]
    private string _selectedTaskMetricLabel = "No run history";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTaskError))]
    private string? _selectedTaskError;

    public bool HasSelectedTaskError => !string.IsNullOrEmpty(SelectedTaskError);

    // ── Authoring ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedTaskCommand))]
    [NotifyPropertyChangedFor(nameof(IsMarkdownReadOnly))]
    private bool _isEditingMarkdown;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private string _editBuffer = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedTaskCommand))]
    private string? _editBlockedReason;

    public bool HasEditBlockedReason => !string.IsNullOrEmpty(EditBlockedReason);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateNewTaskCommand))]
    private string _newTaskTitle = string.Empty;

    [ObservableProperty]
    private string _newTaskBody = string.Empty;

    [ObservableProperty]
    private string? _newTaskError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkdownReadOnly))]
    private bool _isNewTaskDialogOpen;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _logScopeLabel = "full";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateConfigCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAttachmentsCommand))]
    [NotifyPropertyChangedFor(nameof(PauseNoticeText))]
    private bool _isBusy;

    /// <summary>Shows a confirmation dialog. Null (headless tests) skips the prompt.</summary>
    public Func<string, string, Task<bool>>? ShowConfirmationAsync { get; set; }

    // Backend reachability surfaced to the UI. Defaults to reachable so the
    // startup banner stays hidden until a probe says otherwise. The later
    // top-bar status task reuses this state + RefreshBackendStatusAsync rather
    // than running a second probe.
    // Injectable so tests can supply a fake completer; defaults to the frontier proxy.
    public LlmTestCommandFinder TestCommandFinder { get; init; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackendStatusBrush))]
    [NotifyPropertyChangedFor(nameof(BackendStatusLabel))]
    [NotifyCanExecuteChangedFor(nameof(StartBackendCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindTestCommandCommand))]
    private bool _isBackendReachable = true;

    [ObservableProperty]
    private string? _backendStatusMessage;

    public void UseFolderPicker(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
    }

    public void UseFilePicker(IFilePicker filePicker)
    {
        _filePicker = filePicker;
    }

    public async Task LoadInitialAsync()
    {
        LoadObsidianBridgeSettings();

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

        // Populate key states so the key panel and HF gate are accurate from
        // the first moment the window is shown.
        await RefreshKeyStatesAsync();
    }

    // Reusable seam: probes the model backend once and updates the VM state.
    // Safe to call from the UI thread (the probe never throws) and reusable by
    // the later persistent top-bar indicator.
    private async Task RefreshBackendStatusAsync()
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

    // Starts a 1-second timer that refreshes every "elapsed while running" label
    // (running task rows + the active stage card) so a working task visibly ticks.
    // Called ONLY from App startup (never the ctor or LoadInitialAsync) so unit
    // tests spin no timer; they invoke UpdateRunningElapsedLabels() directly.
    public void StartElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += (_, _) => UpdateRunningElapsedLabels();
        _elapsedTimer.Start();
    }

    /// <summary>
    /// Injectable environment accessor. When null (default), all env reads route
    /// through the real process environment. Set only by tests to inject a
    /// <c>DictionaryEnvironmentAccessor</c> without touching any process-global static.
    /// </summary>
    public IEnvironmentAccessor? EnvironmentAccessor { get; init; }

}
