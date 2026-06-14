using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Per-panel collapse flags ──────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(QueueChevron))]
    private bool _isQueueCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(StagesChevron))]
    private bool _isStagesCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(RunLogChevron))]
    [NotifyPropertyChangedFor(nameof(IsActivityColumnCollapsed))]
    private bool _isRunLogCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(LlmCommandsChevron))]
    [NotifyPropertyChangedFor(nameof(IsActivityColumnCollapsed))]
    private bool _isLlmCommandsCollapsed;

    // ── Focus snapshot ─────────────────────────────────────────────────────

    private bool _focusSnapshotQueue;
    private bool _focusSnapshotStages;
    private bool _focusSnapshotRunLog;
    private bool _focusSnapshotLlmCommands;

    // ── Computed properties ────────────────────────────────────────────────

    public bool IsFocused =>
        IsQueueCollapsed && IsStagesCollapsed && IsRunLogCollapsed && IsLlmCommandsCollapsed;

    public bool IsActivityColumnCollapsed =>
        IsRunLogCollapsed && IsLlmCommandsCollapsed;

    public string FocusButtonLabel => IsFocused ? "Restore panels" : "Focus task";
    public string FocusButtonIcon => IsFocused ? "\u2921" : "\u2922"; // ⤡ ⤢
    public string FocusButtonTooltip => IsFocused
        ? "Restore all panels to their previous layout"
        : "Collapse all surrounding panels to maximize the task detail";

    public string QueueChevron => IsQueueCollapsed ? "\u25B6" : "\u25C0"; // ▶ ◀
    public string StagesChevron => IsStagesCollapsed ? "\u25B6" : "\u25C0";
    public string RunLogChevron => IsRunLogCollapsed ? "\u25B6" : "\u25C0";
    public string LlmCommandsChevron => IsLlmCommandsCollapsed ? "\u25B6" : "\u25C0";

    // ── Per-panel toggle commands ──────────────────────────────────────────

    [RelayCommand]
    private void ToggleQueue()
    {
        IsQueueCollapsed = !IsQueueCollapsed;
    }

    [RelayCommand]
    private void ToggleStages()
    {
        IsStagesCollapsed = !IsStagesCollapsed;
    }

    [RelayCommand]
    private void ToggleRunLog()
    {
        IsRunLogCollapsed = !IsRunLogCollapsed;
    }

    [RelayCommand]
    private void ToggleLlmCommands()
    {
        IsLlmCommandsCollapsed = !IsLlmCommandsCollapsed;
    }

    // ── Master focus toggle ────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleFocus()
    {
        if (IsFocused)
        {
            // Restore from snapshot.
            IsQueueCollapsed = _focusSnapshotQueue;
            IsStagesCollapsed = _focusSnapshotStages;
            IsRunLogCollapsed = _focusSnapshotRunLog;
            IsLlmCommandsCollapsed = _focusSnapshotLlmCommands;
        }
        else
        {
            // Snapshot current state, then collapse all.
            _focusSnapshotQueue = IsQueueCollapsed;
            _focusSnapshotStages = IsStagesCollapsed;
            _focusSnapshotRunLog = IsRunLogCollapsed;
            _focusSnapshotLlmCommands = IsLlmCommandsCollapsed;

            IsQueueCollapsed = true;
            IsStagesCollapsed = true;
            IsRunLogCollapsed = true;
            IsLlmCommandsCollapsed = true;
        }
    }
}
