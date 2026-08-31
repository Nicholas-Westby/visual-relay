using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Runs a stage against the model directly, implementing the same
/// <see cref="ISubagentRunner"/> seam the driver has always used — so the
/// driver, its dependencies and the stage-level test doubles need no change.
/// <para>
/// The prompt is built by the SAME builder the previous runner used, so a stage
/// sends byte-identical input. That is what makes an offline differential
/// meaningful: any behavioural difference is the loop's, not the prompt's.
/// </para>
/// <para>
/// Where the previous runner spawned a process, spoke to a proxy over HTTP and
/// inferred what had happened from stdout, this walks the tier's resolved model
/// chain itself and returns a typed result.
/// </para>
/// </summary>
public sealed partial class FirstPartySubagentRunner : ISubagentRunner
{
    private readonly IProviderTransport _transport;
    private readonly RelayConfig _config;
    private readonly IEnvironmentAccessor _environment;
    private readonly IAgentEventSink _events;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _retryBackoffBase;

    /// <summary>Creates a runner.</summary>
    /// <param name="transport">The provider transport to send through.</param>
    /// <param name="config">The repository's relay configuration.</param>
    /// <param name="environment">Where provider keys are read from.</param>
    /// <param name="events">Where the loop's event stream goes.</param>
    /// <param name="tools">The tools the model may call.</param>
    /// <param name="timeProvider">Clock, for virtual-time tests.</param>
    /// <param name="retryBackoffBase">
    /// The first backoff step. Settable so a test can exercise the chain without
    /// waiting real seconds between hops.
    /// </param>
    public FirstPartySubagentRunner(
        IProviderTransport transport,
        RelayConfig config,
        IEnvironmentAccessor environment,
        IAgentEventSink events,
        IReadOnlyList<IAgentTool>? tools = null,
        TimeProvider? timeProvider = null,
        TimeSpan? retryBackoffBase = null)
    {
        _retryBackoffBase = retryBackoffBase;
        _transport = transport;
        _config = config;
        _environment = environment;
        _events = events;
        _tools = tools ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SubagentResult> RunAsync(
        StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var chain = ResolveChain(invocation.Tier);
        if (chain.Count == 0)
            return new SubagentResult(
                string.Empty, null, false,
                $"no model is available for the '{invocation.Tier}' tier: "
                + "no provider key is set for any model in its chain",
                HardAbort: true);

        // Identical to what the previous runner sent, so a differential compares
        // loops rather than prompts.
        var prompt = SwivalSubagentRunner.BuildPrompt(invocation);
        var conversation = new List<ChatMessage>
        {
            new("system", invocation.Stage.SystemPrompt),
            new("user", prompt),
        };

        AgentLoopResult? last = null;
        foreach (var route in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = await RunOnRouteAsync(route, conversation, invocation, cancellationToken)
                .ConfigureAwait(false);

            // A hop is worth taking only for a fault of this route. An exhausted
            // turn budget or a cancelled stage would repeat identically on the
            // next model, and a success is done.
            if (last.Outcome != AgentLoopOutcome.Error) break;

            if (!ReferenceEquals(route, chain[^1]))
                _events.Publish(new AgentEvent(
                    AgentEventKind.Retry, _timeProvider.GetUtcNow(), last.Stats.Turns,
                    Text: last.Error, Detail: "falling through to the next model in the chain"));
        }

        await WriteReportAsync(invocation, last!, cancellationToken).ConfigureAwait(false);
        return ToSubagentResult(invocation, last!);
    }

    /// <summary>
    /// The tier's chain as concrete routes, dropping any model whose key is not
    /// set. Key gating is the catalog's, unchanged: an unbacked vision tier
    /// resolves to nothing rather than degrading to a text model.
    /// </summary>
    private List<ProviderRoute> ResolveChain(string tier)
    {
        var present = BackendConfigGenerator.ProviderKeyNames
            .Where(name => !string.IsNullOrWhiteSpace(_environment.GetEnvironmentVariable(name)))
            .ToHashSet(StringComparer.Ordinal);

        var chains = BackendConfigGenerator.ResolveChains(present, _config.TierModelOverrides);
        if (!chains.TryGetValue(tier, out var models)) return [];

        return [.. models
            .Select(ProviderRoutes.For)
            .OfType<ProviderRoute>()
            .Where(route => present.Contains(route.ApiKeyEnvVar))];
    }

    private static SubagentResult ToSubagentResult(StageInvocation invocation, AgentLoopResult result)
    {
        if (result.Outcome != AgentLoopOutcome.Success)
            return new SubagentResult(
                result.Answer, null, false, result.Error ?? result.OutcomeName,
                // A cancelled stage must not be escalated around: the driver
                // owns that decision and the stage did not fail on its merits.
                HardAbort: result.Outcome == AgentLoopOutcome.Cancelled);

        var contract = StageContractReader.Read(result.Answer, invocation.Stage.OutputContract);
        return new SubagentResult(
            result.Answer, contract.Json, contract.Succeeded, contract.Error);
    }
}
