using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Per-panel collapse flags ──────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(QueueChevron))]
    [NotifyPropertyChangedFor(nameof(QueueRailChevron))]
    [NotifyPropertyChangedFor(nameof(QueueHeaderTooltip))]
    private bool _isQueueCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(StagesChevron))]
    [NotifyPropertyChangedFor(nameof(StagesHeaderTooltip))]
    private bool _isStagesCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocused))]
    [NotifyPropertyChangedFor(nameof(FocusButtonLabel))]
    [NotifyPropertyChangedFor(nameof(FocusButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(ActivityColumnChevron))]
    [NotifyPropertyChangedFor(nameof(ActivityColumnHeaderTooltip))]
    private bool _isActivityColumnCollapsed;

    // ── Activity tab selection ────────────────────────────────────────────

    [ObservableProperty]
    private int _activityTabIndex;

    // ── Focus snapshot ─────────────────────────────────────────────────────

    private bool _focusSnapshotQueue;
    private bool _focusSnapshotStages;
    private bool _focusSnapshotActivity;

    // ── Computed properties ────────────────────────────────────────────────

    public bool IsFocused =>
        IsQueueCollapsed && IsStagesCollapsed && IsActivityColumnCollapsed;

    public string FocusButtonLabel => IsFocused ? "Restore panels" : "Focus task";
    public string FocusButtonTooltip => IsFocused
        ? "Restore all panels to their previous layout"
        : "Collapse all surrounding panels to maximize the task detail";

    // ── Chevron directions (rendered as one shared vector, rotated) ────
    // Only the direction changes per state; the ChevronIcon control draws every
    // chevron at one size and one stroke weight regardless of which way it points.

    /// <summary>Queue (left edge): Left to collapse left, Right to expand.</summary>
    public ChevronDirection QueueChevron =>
        IsQueueCollapsed ? ChevronDirection.Right : ChevronDirection.Left;

    /// <summary>Queue rail expand affordance (always Right — expand right from left rail).</summary>
    public ChevronDirection QueueRailChevron => ChevronDirection.Right;

    /// <summary>Stages (vertical fold): Down expanded, Right collapsed.</summary>
    public ChevronDirection StagesChevron =>
        IsStagesCollapsed ? ChevronDirection.Right : ChevronDirection.Down;

    /// <summary>Activity column (right edge): Down expanded, Right collapsed in-place; Left to expand from rail.</summary>
    public ChevronDirection ActivityColumnChevron =>
        IsActivityColumnCollapsed ? ChevronDirection.Right : ChevronDirection.Down;

    /// <summary>Activity rail expand affordance (always Left — expand left from right edge).</summary>
    public ChevronDirection ActivityRailChevron => ChevronDirection.Left;

    // ── Header toggle tooltips (flip with collapse state) ─────────────────

    public string QueueHeaderTooltip =>
        IsQueueCollapsed ? "Expand Queue" : "Collapse Queue";

    public string StagesHeaderTooltip =>
        IsStagesCollapsed ? "Expand Stages" : "Collapse Stages";

    public string ActivityColumnHeaderTooltip =>
        IsActivityColumnCollapsed ? "Expand Activity" : "Collapse Activity";

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
    private void ToggleActivityColumn()
    {
        IsActivityColumnCollapsed = !IsActivityColumnCollapsed;
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
            IsActivityColumnCollapsed = _focusSnapshotActivity;
        }
        else
        {
            // Snapshot current state, then collapse all.
            _focusSnapshotQueue = IsQueueCollapsed;
            _focusSnapshotStages = IsStagesCollapsed;
            _focusSnapshotActivity = IsActivityColumnCollapsed;

            IsQueueCollapsed = true;
            IsStagesCollapsed = true;
            IsActivityColumnCollapsed = true;
        }
    }
}
