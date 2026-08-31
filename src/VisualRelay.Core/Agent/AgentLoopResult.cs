namespace VisualRelay.Core.Agent;

/// <summary>How one stage ended. Matches the three outcomes in the archived corpus.</summary>
public enum AgentLoopOutcome
{
    /// <summary>The model produced an answer. 1092 of 1109 archived stages.</summary>
    Success,

    /// <summary>
    /// The turn budget ran out with no answer. Seven archived stages, every one
    /// of which the driver escalated by luck rather than by reading this.
    /// </summary>
    Exhausted,

    /// <summary>The stage failed. Ten archived stages.</summary>
    Error,

    /// <summary>
    /// The stage was cancelled: a user stop, or a watchdog kill. Zero archived
    /// stages carry it, not because it never happened but because the old runner
    /// never wrote a report on SIGTERM at all.
    /// </summary>
    Cancelled,
}

/// <summary>
/// What one stage produced. This is the typed result the spec asks for: the
/// driver branches on <see cref="Outcome"/> rather than on an exit code, and
/// reads <see cref="Answer"/> directly rather than extracting a fenced JSON
/// block out of stdout.
/// </summary>
/// <param name="Outcome">How the stage ended.</param>
/// <param name="Answer">The model's final answer, empty when there was none.</param>
/// <param name="Stats">The counters for the report.</param>
/// <param name="Error">
/// Why it failed, when it did. Ten archived failures carried an exact cause that
/// the driver never saw, because all it received was "exit 1".
/// </param>
/// <param name="ServedModel">
/// The concrete model that produced the answer, for cost attribution. A tier
/// alias would hide a fallback hop entirely.
/// </param>
public sealed record AgentLoopResult(
    AgentLoopOutcome Outcome,
    string Answer,
    AgentStats Stats,
    string? Error = null,
    string? ServedModel = null)
{
    /// <summary>
    /// The process-style exit code the driver used to branch on, kept so the
    /// report stays comparable with the archived corpus: 0 success, 2 exhausted,
    /// 1 anything else.
    /// </summary>
    public int ExitCode => Outcome switch
    {
        AgentLoopOutcome.Success => 0,
        AgentLoopOutcome.Exhausted => 2,
        _ => 1,
    };

    /// <summary>The <c>result.outcome</c> string the archived reports carry.</summary>
    public string OutcomeName => Outcome switch
    {
        AgentLoopOutcome.Success => "success",
        AgentLoopOutcome.Exhausted => "exhausted",
        AgentLoopOutcome.Cancelled => "interrupted",
        _ => "error",
    };
}
