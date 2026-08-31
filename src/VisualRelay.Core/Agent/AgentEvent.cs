using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

/// <summary>What happened inside the loop.</summary>
public enum AgentEventKind
{
    /// <summary>A model turn began.</summary>
    TurnStarted,

    /// <summary>
    /// Model output arrived. This is the event that ends the blindness: it fires
    /// while the stage is running, not when the process exits. Measured over 957
    /// archived stages, 99.96% of agent wall time carried no such signal at all.
    /// </summary>
    TokenDelta,

    /// <summary>Reasoning text arrived. Separate, so the UI can fold it away.</summary>
    ReasoningDelta,

    /// <summary>A tool call started.</summary>
    ToolCallStarted,

    /// <summary>A tool call returned, with its real duration.</summary>
    ToolCallFinished,

    /// <summary>A model turn ended.</summary>
    TurnFinished,

    /// <summary>The context was compacted.</summary>
    Compaction,

    /// <summary>The consecutive-error guardrail intervened.</summary>
    GuardrailIntervention,

    /// <summary>The repeat-call storm breaker warned or refused.</summary>
    StormIntervention,

    /// <summary>A model call was retried.</summary>
    Retry,

    /// <summary>The request fell through to another model in the chain.</summary>
    FallbackHop,

    /// <summary>Measured usage for one model call.</summary>
    Usage,

    /// <summary>Tool arguments were repaired before dispatch.</summary>
    ArgumentRepair,

    /// <summary>The model called a tool that does not exist.</summary>
    UnknownTool,
}

/// <summary>
/// One thing that happened in the loop. The trace file, the cost ledger, the
/// watchdog and the live UI all derive from this single stream rather than each
/// inferring state from outside the process.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Timestamp">When, from the injected clock.</param>
/// <param name="Turn">Which turn it belongs to.</param>
/// <param name="Text">Content, reasoning text, or an intervention message.</param>
/// <param name="ToolName">The tool involved, for tool events.</param>
/// <param name="Duration">How long a tool call or model call took.</param>
/// <param name="Usage">Measured usage, on a usage event.</param>
/// <param name="Model">The concrete model served, for cost attribution.</param>
/// <param name="Detail">Anything else worth recording, in one line.</param>
public sealed record AgentEvent(
    AgentEventKind Kind,
    DateTimeOffset Timestamp,
    int Turn,
    string? Text = null,
    string? ToolName = null,
    TimeSpan? Duration = null,
    ProviderUsage? Usage = null,
    string? Model = null,
    string? Detail = null)
{
    /// <summary>
    /// Whether this event is model output, as opposed to a keepalive or a piece
    /// of bookkeeping. The watchdog's output-silence clock resets on these and
    /// only these: that distinction is what a naive port loses, and losing it is
    /// how a wedged request once survived to the absolute ceiling.
    /// </summary>
    public bool IsModelOutput => Kind is AgentEventKind.TokenDelta or AgentEventKind.ReasoningDelta;
}

/// <summary>Receives the loop's event stream.</summary>
public interface IAgentEventSink
{
    /// <summary>
    /// Publishes one event. Implementations must not throw and must not block
    /// the loop: the UI sink coalesces before it reaches the dispatcher, which
    /// is fire-and-forget with no backpressure.
    /// </summary>
    /// <param name="agentEvent">The event.</param>
    void Publish(AgentEvent agentEvent);
}
