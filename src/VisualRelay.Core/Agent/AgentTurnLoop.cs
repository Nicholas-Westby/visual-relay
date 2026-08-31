using System.Text.Json.Nodes;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

/// <summary>
/// The agent loop: ask the model, run the tools it asks for, repeat until it
/// answers or the budget runs out.
/// <para>
/// It runs in this process rather than behind a subprocess, which is what makes
/// the model's output visible while a stage is still running. Every step
/// publishes to <see cref="IAgentEventSink"/>, and the trace, the cost ledger,
/// the watchdog and the UI all derive from that one stream.
/// </para>
/// <para>
/// The answer comes back as a typed result. Nothing here writes a fenced JSON
/// block to stdout for something else to fish back out.
/// </para>
/// </summary>
public sealed partial class AgentTurnLoop
{
    private readonly ChatCompletionClient _client;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly IAgentEventSink _events;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a loop.</summary>
    /// <param name="client">The completion client to ask.</param>
    /// <param name="tools">The tools the model may call.</param>
    /// <param name="events">Where the event stream goes.</param>
    /// <param name="timeProvider">Clock, for virtual-time tests.</param>
    public AgentTurnLoop(
        ChatCompletionClient client,
        IReadOnlyList<IAgentTool> tools,
        IAgentEventSink events,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
        _events = events;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the loop to an answer, to the turn budget, or to a failure.
    /// </summary>
    /// <param name="conversation">The starting messages, oldest first.</param>
    /// <param name="options">Model, endpoint, budgets and tuning.</param>
    /// <param name="toolContext">What the tools need from the run.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The typed stage result.</returns>
    public async Task<AgentLoopResult> RunAsync(
        IReadOnlyList<ChatMessage> conversation,
        AgentLoopOptions options,
        ToolContext toolContext,
        CancellationToken cancellationToken = default)
    {
        var state = new LoopState(options, _timeProvider.GetUtcNow());
        var messages = new List<ChatMessage>(conversation);
        var toolSchemas = _tools.Values
            .Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Definition.Name,
                    ["description"] = t.Definition.Description,
                    ["parameters"] = t.Definition.ParametersSchema.DeepClone(),
                },
            })
            .ToList();

        try
        {
            while (state.Turn < options.MaxTurns)
            {
                cancellationToken.ThrowIfCancellationRequested();

                state.Turn++;
                Publish(AgentEventKind.TurnStarted, state.Turn);

                // Compact against what the provider said the last call actually
                // cost, not against a character estimate of what this one might.
                if (state.Compactor.ShouldCompact(state.LastPromptTokens))
                {
                    var compaction = state.Compactor.Compact(messages);
                    if (compaction.DroppedMessages > 0)
                    {
                        messages = [.. compaction.Messages];
                        Publish(AgentEventKind.Compaction, state.Turn,
                            text: compaction.Note,
                            detail: $"dropped {compaction.DroppedMessages}");
                    }
                }

                var completion = await CallModelAsync(
                    messages, toolSchemas, options, state, cancellationToken).ConfigureAwait(false);

                if (completion is null)
                    return state.Build(AgentLoopOutcome.Error, string.Empty, state.LastError);

                Publish(AgentEventKind.TurnFinished, state.Turn,
                    detail: completion.FinishReason, model: completion.ServedModel);

                // No tool calls means the model is answering.
                if (completion.ToolCalls.Count == 0)
                    return state.Build(AgentLoopOutcome.Success, completion.Content,
                        servedModel: completion.ServedModel);

                messages.Add(new ChatMessage(
                    "assistant",
                    completion.Content,
                    ReasoningContent: completion.ReasoningContent,
                    ToolCalls: completion.ToolCalls));

                foreach (var call in completion.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await DispatchAsync(call, state, toolContext, cancellationToken)
                        .ConfigureAwait(false);
                    messages.Add(new ChatMessage("tool", result, ToolCallId: call.Id));
                }

                if (state.Guardrail.TryIntervene() is { } intervention)
                {
                    state.GuardrailInterventions++;
                    Publish(AgentEventKind.GuardrailIntervention, state.Turn, text: intervention);
                    messages.Add(new ChatMessage("user", intervention));
                }
            }

            // The budget ran out with no answer. This is its own outcome, not an
            // error: seven archived stages ended here and the driver escalated by
            // luck because all it could see was an exit code.
            return state.Build(AgentLoopOutcome.Exhausted, string.Empty,
                $"turn budget of {options.MaxTurns} exhausted without a final answer");
        }
        catch (OperationCanceledException)
        {
            // A report is still written for this path, which the old runner never
            // did: its SIGTERM handler computed "interrupted" and re-raised
            // without persisting, so 1109 archived reports contain zero of them.
            return state.Build(AgentLoopOutcome.Cancelled, string.Empty, "stage cancelled");
        }
    }

    private void Publish(
        AgentEventKind kind, int turn, string? text = null, string? toolName = null,
        TimeSpan? duration = null, ProviderUsage? usage = null, string? model = null,
        string? detail = null) =>
        _events.Publish(new AgentEvent(
            kind, _timeProvider.GetUtcNow(), turn, text, toolName, duration, usage, model, detail));
}
