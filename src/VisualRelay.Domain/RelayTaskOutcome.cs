namespace VisualRelay.Domain;

public enum RelayTaskOutcomeStatus
{
    Committed,
    Flagged,
    Failed,
    Planned
}

public sealed record RelayTaskOutcome(
    string TaskId,
    RelayTaskOutcomeStatus Status,
    string? TaskHash,
    string? CommitSha,
    string? Reason)
{
    /// <summary>
    /// True when the task flagged but its work could not be captured into the
    /// flagged-work bundle: the working tree then holds the only copy, so nothing may
    /// reset it or run another task over it.
    /// </summary>
    public bool WorkUncaptured { get; init; }
}

