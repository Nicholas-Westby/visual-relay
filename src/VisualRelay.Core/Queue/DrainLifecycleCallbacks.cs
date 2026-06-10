using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

/// <summary>
/// Callbacks the GUI wires to receive lifecycle events during a drain so
/// the task rows, stage board, and elapsed timer stay live for both the
/// parallel-planning and serial-execute phases.
/// </summary>
public sealed class DrainLifecycleCallbacks
{
    /// <summary>Called when a task enters the planning phase (Phase 1).</summary>
    public Action<string>? OnPlanningStarted { get; set; }

    /// <summary>Called when a task finishes planning (Planned or Flagged).</summary>
    public Action<string, RelayTaskOutcomeStatus>? OnPlanningCompleted { get; set; }

    /// <summary>Called when a task enters the serial execute phase (Phase 2).</summary>
    public Action<string>? OnExecuteStarted { get; set; }

    /// <summary>Called when a task finishes execution (Committed, Flagged, or Failed).</summary>
    public Action<string, RelayTaskOutcomeStatus>? OnExecuteCompleted { get; set; }
}
