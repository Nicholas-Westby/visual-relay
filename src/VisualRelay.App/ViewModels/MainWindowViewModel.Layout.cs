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
    [NotifyPropertyChangedFor(nameof(QueueRailChevron))]
    [NotifyPropertyChangedFor(nameof(QueueHeaderTooltip))]
    private bool _isQueueCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(StagesChevron))]
    [NotifyPropertyChangedFor(nameof(StagesHeaderTooltip))]
    private bool _isStagesCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(RunLogChevron))]
    [NotifyPropertyChangedFor(nameof(RunLogHeaderTooltip))]
    [NotifyPropertyChangedFor(nameof(LlmCommandsChevron))]
    [NotifyPropertyChangedFor(nameof(IsActivityColumnCollapsed))]
    private bool _isRunLogCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonIcon))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(LlmCommandsChevron))]
    [NotifyPropertyChangedFor(nameof(LlmCommandsHeaderTooltip))]
    [NotifyPropertyChangedFor(nameof(RunLogChevron))]
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

    // ── Chevron glyphs (direction- and axis-correct) ──────────────────────

    /// <summary>Queue (left edge): ◀ collapse left, ▶ expand right.</summary>
    public string QueueChevron => IsQueueCollapsed ? "\u25B6" : "\u25C0"; // ▶ ◀

    /// <summary>Queue rail expand glyph (always ▶ — expand right from left rail).</summary>
    public string QueueRailChevron => "\u25B6"; // ▶

    /// <summary>Stages (vertical fold): ▾ expanded, ▸ collapsed.</summary>
    public string StagesChevron => IsStagesCollapsed ? "\u25B8" : "\u25BE"; // ▸ ▾

    /// <summary>
    /// Run Log header: when the column slides to the right rail (both collapsed)
    /// show ▶; otherwise use vertical disclosure ▸/▾ for the in-place fold.
    /// </summary>
    public string RunLogChevron => IsActivityColumnCollapsed
        ? "\u25B6" // ▶ slide right
        : IsRunLogCollapsed ? "\u25B8" : "\u25BE"; // ▸ ▾

    /// <summary>
    /// LLM Commands header: same dual-mode scheme as Run Log.
    /// </summary>
    public string LlmCommandsChevron => IsActivityColumnCollapsed
        ? "\u25B6" // ▶ slide right
        : IsLlmCommandsCollapsed ? "\u25B8" : "\u25BE"; // ▸ ▾

    /// <summary>Activity rail expand glyph (always ◀ — expand left from right edge).</summary>
    public string ActivityRailChevron => "\u25C0"; // ◀

    // ── Header toggle tooltips (flip with collapse state) ─────────────────

    public string QueueHeaderTooltip =>
        IsQueueCollapsed ? "Expand Queue" : "Collapse Queue";

    public string StagesHeaderTooltip =>
        IsStagesCollapsed ? "Expand Stages" : "Collapse Stages";

    public string RunLogHeaderTooltip =>
        IsRunLogCollapsed ? "Expand Run Log" : "Collapse Run Log";

    public string LlmCommandsHeaderTooltip =>
        IsLlmCommandsCollapsed ? "Expand LLM Commands" : "Collapse LLM Commands";

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
