namespace VisualRelay.Domain;

public enum RelayTaskOutcomeStatus
{
    Committed,
    Flagged,
    Failed
}

public sealed record RelayTaskOutcome(
    string TaskId,
    RelayTaskOutcomeStatus Status,
    string? TaskHash,
    string? CommitSha,
    string? Reason);

