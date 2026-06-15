namespace VisualRelay.Core.Tasks;

/// <summary>
/// Result of a task-retirement filesystem operation, returned before the git
/// commit so the new retirement paths can be included in the same commit.
/// </summary>
/// <param name="Additions">Relative paths to new files that must be staged
/// (e.g. the DONE- or archived paths).</param>
/// <param name="Rollback">Delegate that reverses the rename/move. Invoke on
/// commit failure to keep the task runnable. <c>null</c> when no undo is
/// needed (already-retired case).</param>
/// <param name="WasRetired"><c>true</c> when files were actually moved;
/// <c>false</c> when the task was already retired (idempotent no-op).</param>
/// <param name="DestinationPath">Final path of the retired file/directory
/// (for event publishing).</param>
internal sealed record RetirementResult(
    IReadOnlyList<string> Additions,
    Action? Rollback,
    // ReSharper disable once NotAccessedPositionalProperty.Global — part of the
    // result contract (idempotent no-op vs. real move); set at both construction
    // sites for callers even though no current consumer branches on it.
    bool WasRetired,
    string DestinationPath);
