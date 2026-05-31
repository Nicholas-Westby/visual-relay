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
    Failed
}
