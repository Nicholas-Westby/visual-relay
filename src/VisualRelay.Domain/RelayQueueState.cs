namespace VisualRelay.Domain;

public enum RelayQueueState
{
    Idle,
    Refreshing,
    Running,
    PauseRequested,
    Paused,
    ReviewNeeded,
    Completed,
    Failed,

    /// <summary>An operator cancelled the drain; it stopped without halting.</summary>
    Cancelled
}
