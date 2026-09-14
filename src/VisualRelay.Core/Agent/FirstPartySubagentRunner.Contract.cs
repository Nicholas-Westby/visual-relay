using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

public sealed partial class FirstPartySubagentRunner
{
    /// <summary>
    /// Reads the stage's contract, and when the answer holds no readable JSON at
    /// all or lacks a required key, spends ONE follow-up turn on the same session
    /// asking for a corrected block.
    /// <para>
    /// Across a 27-task run, five stages were discarded on a first read whose
    /// reasoning and code were both correct and already self-verified, and not
    /// one of them was asked again. A single turn is cheap next to re-running the
    /// stage, and cheaper still than losing the work.
    /// </para>
    /// </summary>
    /// <param name="route">The model that answered, so the re-ask goes to it.</param>
    /// <param name="conversation">The stage's opening messages.</param>
    /// <param name="invocation">The stage being run.</param>
    /// <param name="result">What the loop produced.</param>
    /// <param name="events">The stage's event stream, which the re-ask joins.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The result with the re-ask folded in, and the contract read.</returns>
    private async Task<(AgentLoopResult Result, StageContractResult Contract)> ResolveContractAsync(
        ProviderRoute route,
        IReadOnlyList<ChatMessage> conversation,
        StageInvocation invocation,
        AgentLoopResult result,
        IAgentEventSink events,
        CancellationToken cancellationToken)
    {
        var contract = StageContractReader.Read(result.Answer, invocation.Stage.OutputContract);
        AnnounceRepairs(invocation, contract.Repairs);
        if (contract is { Unparseable: false, MissingKey: null }) return (result, contract);

        AnnounceReAsk(invocation, contract.Error);
        var retry = await ReAskAsync(
            route, conversation, invocation, result.Answer, contract.Error, contract.MissingKey, events,
            cancellationToken).ConfigureAwait(false);

        // The turn was spent either way, so it counts toward the stage either
        // way: the report is what the ledger and the cost card are built from.
        var folded = result with { Stats = result.Stats.Plus(retry.Stats) };
        if (retry.Outcome != AgentLoopOutcome.Success) return (folded, contract);

        var second = StageContractReader.Read(retry.Answer, invocation.Stage.OutputContract);
        AnnounceRepairs(invocation, second.Repairs);

        // A failed re-ask keeps the FIRST complaint: it describes the answer the
        // artifacts actually hold, which is what someone reading the flag needs.
        return (folded, second.Succeeded ? second : contract);
    }

    /// <summary>
    /// One turn, no tools, same model: the model is asked for the corrected block
    /// and nothing else.
    /// </summary>
    private async Task<AgentLoopResult> ReAskAsync(
        ProviderRoute route,
        IReadOnlyList<ChatMessage> conversation,
        StageInvocation invocation,
        string answer,
        string? error,
        string? missingKey,
        IAgentEventSink events,
        CancellationToken cancellationToken)
    {
        var apiKey = _keys.Resolve(route.ApiKeyEnvVar) ?? string.Empty;
        var client = new ChatCompletionClient(_transport, route.Timeouts, _timeProvider);
        var budget = StageBudget(route, invocation);

        // No tools. The model has nothing left to look up: it is being asked to
        // re-emit what it already wrote, in valid JSON, and a tool call here
        // would only spend the one turn it has.
        var loop = new AgentTurnLoop(client, [], events, _timeProvider);
        var options = new AgentLoopOptions(
            Model: route.UpstreamModel,
            Endpoint: route.Endpoint,
            Headers: route.Headers(apiKey),
            Capabilities: ProviderCapabilityCatalog.For(route.ProviderName),
            MaxTurns: 1,
            StageBudget: budget,
            RetryBudget: RetriesPerRoute,
            ContextWindow: route.ContextWindow,
            RetryBackoffBase: _retryBackoffBase);

        var messages = new List<ChatMessage>(conversation)
        {
            new("assistant", answer),
            new("user", ReAskPrompt(error, missingKey, invocation.Stage.OutputContract)),
        };

        return await loop.RunAsync(
            messages, options, new ToolContext(invocation.TargetRoot, budget), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The follow-up turn's instruction. It names the parser's own complaint and
    /// asks for the block alone: repeating the reasoning would spend the turn's
    /// output budget on text nothing reads. A missing key is filled from what the
    /// answer already says, never from new work.
    /// </summary>
    private static string ReAskPrompt(string? error, string? missingKey, string contract) =>
        missingKey is not null
            ? $"""
              Your reply's contract block could not be read: {error}.

              Reply with ONLY the corrected fenced json block and nothing else — no
              preamble, no explanation, no second block. Keep every key it already has
              exactly as it is, and add "{missingKey}" with the value your reply already
              describes. Do not do any new work to fill it in.

              {contract}
              """
            : $"""
        Your reply's contract block could not be read: {error}.

        Reply with ONLY the corrected fenced json block and nothing else — no
        preamble, no explanation, no second block. Do not change what it says;
        change only the JSON so that it parses. In particular: escape every
        newline, tab and quote that appears inside a string value, double every
        backslash that is not one of \" \\ \/ \b \f \n \r \t \uXXXX, and remove
        any comma that sits before a closing brace or bracket.

        {contract}
        """;

    /// <summary>Announces that a stage's contract only parsed after repair.</summary>
    /// <param name="invocation">The stage whose contract was repaired.</param>
    /// <param name="repairs">What had to be repaired, already deduplicated.</param>
    /// <remarks>
    /// A warning rather than a note: the stage keeps its work, but a model that
    /// cannot emit valid JSON for its own contract is a defect, and the run log
    /// is where that becomes visible across a drain rather than one stage at a
    /// time.
    /// </remarks>
    private void AnnounceRepairs(StageInvocation invocation, IReadOnlyList<string> repairs)
    {
        if (repairs.Count == 0) return;
        Announce(invocation, "warn", "contract_repaired", "repairs", string.Join(", ", repairs));
    }

    /// <summary>Announces the one follow-up turn, with the error that prompted it.</summary>
    private void AnnounceReAsk(StageInvocation invocation, string? error) =>
        Announce(invocation, "info", "contract_reask", "error", error ?? "unreadable");

    private void Announce(
        StageInvocation invocation, string level, string name, string key, string value) =>
        _relayEvents?.PublishAsync(
            new RelayEvent(
                _timeProvider.GetUtcNow(), level, name,
                invocation.RunId, invocation.TargetRoot, invocation.TaskName,
                invocation.Stage.Number, invocation.Tier,
                Data: new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }),
            CancellationToken.None);
}
