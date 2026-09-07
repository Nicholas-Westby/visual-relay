using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VisualRelay.App.ViewModels;

/// <summary>
/// Stopping a run without killing the app. Every run the app starts — Run, Resume
/// and Run all alike — executes inside one cancellation scope, and this is what
/// cancels it. The run then winds itself down: the driver restores the tree it was
/// editing and marks the task for review, so the next run starts from a known state.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The live run's cancellation source; null whenever no run is active.</summary>
    private CancellationTokenSource? _runCancellation;

    /// <summary>The task the cancel interrupted, kept so the status line can name it.</summary>
    private string? _cancelledTaskId;

    /// <summary>
    /// True from the moment a cancel is requested until the run has finished winding
    /// down. Surfaced on <c>/state</c> so a caller can tell "stopping" from "stopped".
    /// </summary>
    [ObservableProperty]
    private bool _cancelRequested;

    private bool CanCancelRun() => _runCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun()
    {
        if (_runCancellation is not { } cancellation)
        {
            return;
        }

        _cancelledTaskId = _runningTaskId;
        CancelRequested = true;
        StatusText = _cancelledTaskId is { } id ? $"Cancelling {id}…" : "Cancelling the run…";
        cancellation.Cancel();
    }

    /// <summary>
    /// Runs <paramref name="action"/> as THE cancellable run: it owns the token the
    /// pipeline threads all the way down to the stage subprocesses, and it is what
    /// <see cref="CancelRunCommand"/> cancels. The run itself is responsible for
    /// winding down cleanly; this only reports the outcome on the status line.
    /// </summary>
    private Task RunCancellableAsync(Func<CancellationToken, Task> action) =>
        RunBusyAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            CancelRunCommand.NotifyCanExecuteChanged();
            try
            {
                await action(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Expected: a run that surfaces the cancel rather than absorbing it.
                // The wind-down has already run inside the driver.
            }
            finally
            {
                _runCancellation = null;
                CancelRunCommand.NotifyCanExecuteChanged();
                if (CancelRequested)
                {
                    StatusText = _cancelledTaskId is { } id ? $"Cancelled {id}" : "Cancelled the run";
                    _cancelledTaskId = null;
                    CancelRequested = false;
                }
            }
        });

    /// <summary>
    /// Test seam: runs <paramref name="action"/> in the same cancellable scope every
    /// production run uses, so the cancel command can be driven end to end without a
    /// pipeline. Only tests call it.
    /// </summary>
    internal Task RunCancellableForTestsAsync(Func<CancellationToken, Task> action) =>
        RunCancellableAsync(action);
}
