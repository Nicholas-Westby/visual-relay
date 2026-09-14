namespace VisualRelay.App.ViewModels;

/// <summary>
/// One thing that keeps a run command disabled. The run commands' enabled state is
/// derived from these, so what the control API reports as the reason is what the button
/// itself checks.
/// </summary>
public enum RunBlocker
{
    /// <summary>A run is already in progress.</summary>
    Busy,

    /// <summary>A pause is armed or in effect; it outlives the drain it stopped.</summary>
    Paused,

    /// <summary>The archive is showing instead of the queue.</summary>
    ArchiveShowing,

    /// <summary>The queue has no tasks.</summary>
    QueueEmpty,

    /// <summary>No task is selected.</summary>
    NothingSelected,

    /// <summary>The selected task is archived.</summary>
    SelectedArchived,

    /// <summary>The selected task is being rewritten.</summary>
    SelectedRewriting,

    /// <summary>The selected task is not flagged, and only a flagged task can be reset.</summary>
    SelectedNotFlagged,
}
