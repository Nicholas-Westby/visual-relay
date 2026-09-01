using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;
using VisualRelay.Core.Logging;
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
    private readonly ProviderKeyResolver _keys;
    private readonly Func<StageInvocation, IAgentEventSink> _events;

    private readonly IRelayEventSink? _relayEvents;

    private readonly IGitInvoker _git;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _retryBackoffBase;

    /// <summary>Creates a runner.</summary>
    /// <param name="transport">The provider transport to send through.</param>
    /// <param name="config">The repository's relay configuration.</param>
    /// <param name="environment">
    /// The process environment. Keys are resolved through it AND through the
    /// user-level dotenv, because that is where the key panel writes them.
    /// </param>
    /// <param name="events">
    /// Builds the event sink for a stage. It is per-invocation because the sink
    /// labels each event with the stage it belongs to, and the stage is not
    /// known until the driver calls in.
    /// </param>
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
        Func<StageInvocation, IAgentEventSink> events,
        IReadOnlyList<IAgentTool>? tools = null,
        TimeProvider? timeProvider = null,
        TimeSpan? retryBackoffBase = null,
        IRelayEventSink? relayEvents = null,
        IGitInvoker? git = null)
    {
        _relayEvents = relayEvents;
        _git = git ?? new GitInvoker();
        _retryBackoffBase = retryBackoffBase;
        _transport = transport;
        _config = config;
        _keys = new ProviderKeyResolver(environment);
        _events = events;
        _tools = tools ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SubagentResult> RunAsync(
        StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var events = _events(invocation);
        var chain = ResolveChain(invocation.Tier);
        if (chain.Count == 0)
            return new SubagentResult(
                string.Empty, null, false,
                $"no model is available for the '{invocation.Tier}' tier: "
                + "no provider key is set for any model in its chain",
                HardAbort: true);

        // Identical to what the previous runner sent, so a differential compares
        // loops rather than prompts.
        var prompt = SandboxedStage.BuildPrompt(invocation);
        WriteStageInput(invocation, prompt);
        var conversation = new List<ChatMessage>
        {
            new("system", invocation.Stage.SystemPrompt),
            new("user", prompt),
        };

        AgentLoopResult? last = null;
        foreach (var route in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = await RunOnRouteAsync(route, conversation, invocation, events, cancellationToken)
                .ConfigureAwait(false);

            // A hop is worth taking only for a fault of this route. An exhausted
            // turn budget or a cancelled stage would repeat identically on the
            // next model, and a success is done.
            if (last.Outcome != AgentLoopOutcome.Error) break;

            if (!ReferenceEquals(route, chain[^1]))
                events.Publish(new AgentEvent(
                    AgentEventKind.Retry, _timeProvider.GetUtcNow(), last.Stats.Turns,
                    Text: last.Error, Detail: "falling through to the next model in the chain"));
        }

        await WriteReportAsync(invocation, last!, events, cancellationToken).ConfigureAwait(false);
        return await ToSubagentResultAsync(invocation, last!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tier's chain as concrete routes, dropping any model whose key is not
    /// set. Key gating is the catalog's, unchanged: an unbacked vision tier
    /// resolves to nothing rather than degrading to a text model.
    /// </summary>
    private List<ProviderRoute> ResolveChain(string tier)
    {
        var present = _keys.PresentKeys();

        var chains = BackendConfigGenerator.ResolveChains(present, _config.TierModelOverrides);
        if (!chains.TryGetValue(tier, out var models)) return [];

        return [.. models
            .Select(ProviderRoutes.For)
            .OfType<ProviderRoute>()
            .Where(route => present.Contains(route.ApiKeyEnvVar))];
    }

    private async Task<SubagentResult> ToSubagentResultAsync(
        StageInvocation invocation, AgentLoopResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome != AgentLoopOutcome.Success)
            return new SubagentResult(
                result.Answer, null, false, result.Error ?? result.OutcomeName,
                // A cancelled stage must not be escalated around: the driver
                // owns that decision and the stage did not fail on its merits.
                HardAbort: result.Outcome == AgentLoopOutcome.Cancelled);

        var contract = StageContractReader.Read(result.Answer, invocation.Stage.OutputContract);
        if (!contract.Succeeded || contract.Json is not { } json)
            return new SubagentResult(result.Answer, contract.Json, contract.Succeeded, contract.Error);

        // Stages 4 and 10 name the files they intend to change. A gitignored or
        // absent path there produces a manifest the commit stage cannot honour,
        // so it is rejected here and the corrective message tells the model what
        // to fix. The subprocess runner did this at the same point; it is a
        // property of the contract, not of how the stage was run.
        if (invocation.Stage.Number is 4 or 10)
        {
            var manifestError = await SandboxedStage.CheckManifestAgainstGitignoreAsync(
                json, invocation.Stage.Number, invocation.TargetRoot, _git, cancellationToken)
                .ConfigureAwait(false);
            if (manifestError is not null)
                return new SubagentResult(result.Answer, null, false, manifestError);
        }

        return new SubagentResult(result.Answer, json, true, contract.Error);
    }
}
