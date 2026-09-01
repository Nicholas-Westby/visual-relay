using VisualRelay.Core.Llm;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Publishes the loop's events onto the existing relay event stream, so the UI,
/// the run log and the trace file all see them.
/// <para>
/// This is where the blindness ends. The path it feeds — sink, dispatcher,
/// observable collection, Commands tab — already worked; it was simply never
/// fed until a stage had exited, because the previous runner wrote its whole
/// transcript at process exit. Measured over 957 archived stages, 99.96% of
/// agent wall time carried no live signal at all. These events are published as
/// they happen.
/// </para>
/// <para>
/// Events are emitted in the shape the UI already parses: an event named
/// <c>trace</c> carrying <c>kind</c>, <c>title</c> and <c>content</c>, so no new
/// protocol is introduced and nothing downstream needs to change.
/// </para>
/// </summary>
/// <param name="sink">The relay event sink to publish onto.</param>
/// <param name="invocation">The stage these events belong to.</param>
public sealed class RelayEventBridge(IRelayEventSink sink, StageInvocation invocation)
    : IAgentEventSink
{
    /// <summary>
    /// How much of a single trace entry is kept. The previous path clipped at
    /// the same size; an unbounded tool result would otherwise sit in an
    /// observable collection for the life of the run.
    /// </summary>
    private const int MaxTraceContent = 1500;

    /// <inheritdoc />
    public void Publish(AgentEvent agentEvent)
    {
        var entry = ToTraceEntry(agentEvent);
        if (entry is null) return;

        // Fire and forget deliberately: the loop must never block on the UI, and
        // the sink already isolates its own failures.
        _ = sink.PublishAsync(new RelayEvent(
            agentEvent.Timestamp,
            agentEvent.Kind == AgentEventKind.UnknownTool ? "warn" : "info",
            "trace",
            invocation.RunId,
            invocation.TargetRoot,
            invocation.TaskName,
            invocation.Stage.Number,
            invocation.Tier,
            Data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = entry.Kind.ToString(),
                ["title"] = entry.Title,
                ["content"] = Trim(entry.Content),
            }));
    }

    /// <summary>
    /// Maps a loop event onto the trace vocabulary the UI already renders.
    /// Bookkeeping events that would only add noise return null.
    /// </summary>
    private static TraceEntry? ToTraceEntry(AgentEvent agentEvent) => agentEvent.Kind switch
    {
        AgentEventKind.TokenDelta =>
            new TraceEntry(TraceEntryKind.AssistantText, "text", agentEvent.Text ?? string.Empty),

        AgentEventKind.ReasoningDelta =>
            new TraceEntry(TraceEntryKind.Thinking, "thinking", agentEvent.Text ?? string.Empty),

        AgentEventKind.ToolCallStarted =>
            new TraceEntry(
                TraceEntryKind.ToolCall, agentEvent.ToolName ?? "tool", agentEvent.Text ?? string.Empty),

        AgentEventKind.ToolCallFinished =>
            new TraceEntry(
                TraceEntryKind.ToolResult,
                agentEvent.ToolName ?? "tool",
                agentEvent.Duration is { } duration
                    ? $"{agentEvent.Detail} in {duration.TotalSeconds:0.00}s"
                    : agentEvent.Detail ?? string.Empty),

        // The interventions are worth seeing: they explain why a run changed
        // course, which the previous path never surfaced at all.
        AgentEventKind.Compaction or AgentEventKind.GuardrailIntervention
            or AgentEventKind.StormIntervention or AgentEventKind.Retry
            or AgentEventKind.UnknownTool or AgentEventKind.ArgumentRepair =>
            new TraceEntry(
                TraceEntryKind.UserText,
                Describe(agentEvent.Kind),
                agentEvent.Text ?? agentEvent.Detail ?? string.Empty),

        // The one place a run says which concrete model answered and what it
        // measured. Without this the trace and the run log named only the tier
        // alias, so a fallback hop and its cost were invisible to anyone
        // reading them.
        AgentEventKind.Usage when agentEvent.Usage is { } usage =>
            new TraceEntry(TraceEntryKind.UserText, "usage", DescribeUsage(agentEvent.Model, usage)),

        _ => null,
    };

    /// <summary>One line naming the model that answered and what it counted.</summary>
    /// <param name="model">The concrete model the provider named, if it named one.</param>
    /// <param name="usage">The measured usage for the call.</param>
    /// <returns>A line for the trace.</returns>
    private static string DescribeUsage(string? model, ProviderUsage usage)
    {
        var parts = new List<string>
        {
            $"model {model ?? "unreported"}",
            $"in {usage.PromptTokens}",
            $"out {usage.CompletionTokens}",
        };

        if (usage.CachedTokens > 0) parts.Add($"cached {usage.CachedTokens}");
        if (usage.CacheWriteTokens > 0) parts.Add($"cache-write {usage.CacheWriteTokens}");
        if (usage.ReasoningTokens > 0) parts.Add($"reasoning {usage.ReasoningTokens}");

        return string.Join("  ", parts);
    }

    private static string Describe(AgentEventKind kind) => kind switch
    {
        AgentEventKind.Compaction => "context compacted",
        AgentEventKind.GuardrailIntervention => "guardrail",
        AgentEventKind.StormIntervention => "repeat detected",
        AgentEventKind.Retry => "retry",
        AgentEventKind.UnknownTool => "unknown tool",
        _ => "arguments repaired",
    };

    private static string Trim(string content) =>
        content.Length <= MaxTraceContent ? content : content[..MaxTraceContent] + "…";
}
