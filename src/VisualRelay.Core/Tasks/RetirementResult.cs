namespace VisualRelay.Core.Tasks;

/// <summary>
/// Result of a task-retirement filesystem operation, returned before the git
/// commit so the new retirement paths can be included in the same commit.
/// </summary>
internal sealed record RetirementResult(
    /// <summary>Relative paths to new files that must be staged (e.g. the
    /// DONE- or archived paths).</summary>
    IReadOnlyList<string> Additions,
    /// <summary>Delegate that reverses the rename/move. Invoke on commit
    /// failure to keep the task runnable. <c>null</c> when no undo is
    /// needed (already-retired case).</summary>
    Action? Rollback,
    /// <summary><c>true</c> when files were actually moved; <c>false</c>
    /// when the task was already retired (idempotent no-op).</summary>
    bool WasRetired,
    /// <summary>Final path of the retired file/directory (for event
    /// publishing).</summary>
    string DestinationPath);
