using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// Hosts the task queue and its drag-to-reorder gesture. The gesture/visual code
/// lives here; the actual list mutation routes through
/// <see cref="MainWindowViewModel.MoveTask"/> so the reorder logic stays testable
/// without driving pointer input. Dragging is gated off while the runner is busy
/// or the archive is shown (mirroring the old Up/Down command gate).
/// </summary>
public partial class QueuePanel : UserControl
{
    // In-process format: the dragged row reference never leaves the app, so it is
    // never serialized to the platform clipboard / OS drag buffer.
    private static readonly DataFormat<TaskRowViewModel> TaskRowFormat =
        DataFormat.CreateInProcessFormat<TaskRowViewModel>("visual-relay/task-row");

    private ListBox? _list;
    private ListBoxItem? _dropTarget;

    public QueuePanel()
    {
        InitializeComponent();
    }

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_list is not null)
        {
            return;
        }

        _list = this.FindControl<ListBox>("TaskQueueList");
        if (_list is null)
        {
            return;
        }

        // Tunnel the press so the drag can start before the ListBox consumes it.
        _list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        _list.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private bool ReorderEnabled =>
        DataContext is MainWindowViewModel { IsBusy: false, ShowArchive: false };

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReorderEnabled ||
            !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed ||
            ItemUnder(e.GetPosition(_list))?.DataContext is not TaskRowViewModel row)
        {
            return;
        }

        // Fire-and-forget: the drag loop runs to completion on its own. Faults are
        // swallowed inside BeginDragAsync so none escape the synchronous handler.
        _ = BeginDragAsync(e, row);
    }

    private async Task BeginDragAsync(PointerPressedEventArgs e, TaskRowViewModel row)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TaskRowFormat, row));
        try
        {
            // Returns once the user releases. Avalonia only promotes this to a real
            // drag after the system threshold, so a plain click still selects.
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            // A failed drag is never fatal — abort the gesture rather than crash.
            System.Diagnostics.Trace.WriteLine($"QueuePanel drag aborted: {ex.Message}");
        }
        finally
        {
            ClearDropTarget();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!ReorderEnabled || !e.DataTransfer.Contains(TaskRowFormat))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropTarget();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        UpdateDropTarget(e);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (DataContext is not MainWindowViewModel vm ||
            e.DataTransfer.TryGetValue(TaskRowFormat) is not { } dragged)
        {
            return;
        }

        var from = vm.Tasks.IndexOf(dragged);
        var to = ResolveTargetIndex(e, from);
        if (from >= 0 && to >= 0)
        {
            vm.MoveTask(from, to);
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => ClearDropTarget();

    private void UpdateDropTarget(DragEventArgs e)
    {
        if (_list is null)
        {
            return;
        }

        var item = ItemUnder(e.GetPosition(_list));
        ClearDropTarget();
        if (item is null)
        {
            return;
        }

        _dropTarget = item;
        var below = IsLowerHalf(e, item);
        item.Classes.Set("drop-below", below);
        item.Classes.Set("drop-above", !below);
    }

    private int ResolveTargetIndex(DragEventArgs e, int from)
    {
        if (_list is null || DataContext is not MainWindowViewModel vm || from < 0)
        {
            return -1;
        }

        var item = ItemUnder(e.GetPosition(_list));
        if (item?.DataContext is not TaskRowViewModel overRow)
        {
            // Dropped past the last row → move to the end.
            return vm.Tasks.Count - 1;
        }

        var overIndex = vm.Tasks.IndexOf(overRow);
        if (overIndex < 0)
        {
            return -1;
        }

        var target = IsLowerHalf(e, item) ? overIndex + 1 : overIndex;
        // Removing the dragged row first shifts everything after it down by one.
        if (from < target)
        {
            target--;
        }

        return Math.Clamp(target, 0, vm.Tasks.Count - 1);
    }

    private static bool IsLowerHalf(DragEventArgs e, ListBoxItem item) =>
        e.GetPosition(item).Y > item.Bounds.Height / 2;

    private ListBoxItem? ItemUnder(Point position) =>
        (_list?.InputHitTest(position) as Visual)?.FindAncestorOfType<ListBoxItem>();

    private void ClearDropTarget()
    {
        if (_dropTarget is null)
        {
            return;
        }

        _dropTarget.Classes.Set("drop-above", false);
        _dropTarget.Classes.Set("drop-below", false);
        _dropTarget = null;
    }
}
