namespace VisualRelay.Core.Queue;

/// <summary>
/// Controls how the Run All (drain queue) operation executes tasks.
/// </summary>
public enum RunAllMode
{
    /// <summary>Planning stages (1–4) run in parallel, then execution stages
    /// (5–11) run sequentially — the default behaviour.</summary>
    Standard,

    /// <summary>Every task runs in full sequence, one at a time, with no
    /// parallel planning phase.</summary>
    Sequential,

    /// <summary>Sequential, plus the app rebuilds and relaunches after each
    /// committed task so the next task picks up the new build.</summary>
    RestartBetweenTasks,
}
