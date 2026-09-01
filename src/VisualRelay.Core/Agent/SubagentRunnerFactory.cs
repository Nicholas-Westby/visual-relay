using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Builds the agent that runs a stage.
/// <para>
/// Every call site goes through here, which is what made the cutover from the
/// Swival subprocess one decision rather than eight. There is no selector any
/// more: the in-process loop is the only agent.
/// </para>
/// </summary>
public static class SubagentRunnerFactory
{
    /// <summary>
    /// Builds the runner for a stage.
    /// </summary>
    /// <param name="config">The repository's relay configuration.</param>
    /// <param name="sink">Where run events are published.</param>
    /// <param name="environment">Where provider keys are read from.</param>
    /// <param name="verboseDiagnostics">Whether to keep sandbox diagnostics.</param>
    /// <returns>The runner to use.</returns>
    public static ISubagentRunner Create(
        RelayConfig config,
        IRelayEventSink sink,
        IEnvironmentAccessor environment,
        bool verboseDiagnostics = false) =>
        new FirstPartySubagentRunner(
            LiveProviderTransport.CreateDefault(),
            config,
            environment,
            stage => new CoalescingAgentEventSink(new RelayEventBridge(sink, stage)),
            BuildTools(config, verboseDiagnostics),
            relayEvents: sink);

    /// <summary>
    /// The fourteen tools, wired to the sandboxed executor. Every command tool
    /// runs through the same hardened prefix builder the verify path uses, with
    /// no opt-out.
    /// </summary>
    private static IReadOnlyList<IAgentTool> BuildTools(RelayConfig config, bool verboseDiagnostics)
    {
        var catalog = new ToolCatalog();
        FileToolset.RegisterInto(catalog);
        CommandToolset.RegisterInto(
            catalog, new SandboxedCommandExecutor(config, verboseDiagnostics: verboseDiagnostics));
        return catalog.Tools;
    }
}
