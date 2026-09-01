using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Chooses which agent runs a stage, and builds it.
/// <para>
/// Every call site that used to construct the subprocess runner goes through
/// here, so the cutover is one decision in one place rather than eight. Until
/// the first-party loop has been measured against real work it is opt-in:
/// setting <c>VR_AGENT=firstparty</c> selects it, anything else keeps the
/// existing behaviour. That is what makes a side-by-side comparison possible on
/// real drains without a branch.
/// </para>
/// </summary>
public static class SubagentRunnerFactory
{
    /// <summary>The environment variable that selects the agent.</summary>
    public const string SelectorEnvVar = "VR_AGENT";

    /// <summary>The value of <see cref="SelectorEnvVar"/> that selects the new loop.</summary>
    public const string FirstPartyValue = "firstparty";

    /// <summary>
    /// Whether the first-party loop is selected.
    /// </summary>
    /// <param name="environment">Where the selector is read from.</param>
    /// <returns>True when the first-party loop should run the stage.</returns>
    public static bool UsesFirstParty(IEnvironmentAccessor environment) =>
        string.Equals(
            environment.GetEnvironmentVariable(SelectorEnvVar),
            FirstPartyValue,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the runner for a stage.
    /// </summary>
    /// <param name="config">The repository's relay configuration.</param>
    /// <param name="sink">Where run events are published.</param>
    /// <param name="environment">Where keys and the selector are read from.</param>
    /// <param name="verboseDiagnostics">Whether to keep sandbox diagnostics.</param>
    /// <returns>The runner to use.</returns>
    public static ISubagentRunner Create(
        RelayConfig config,
        IRelayEventSink sink,
        IEnvironmentAccessor environment,
        bool verboseDiagnostics = false)
    {
        if (!UsesFirstParty(environment))
            return new SwivalSubagentRunner(
                config, new GitInvoker(), eventSink: sink, verboseDiagnostics: verboseDiagnostics);

        return new FirstPartySubagentRunner(
            LiveProviderTransport.CreateDefault(),
            config,
            environment,
            stage => new CoalescingAgentEventSink(new RelayEventBridge(sink, stage)),
            BuildTools(config, verboseDiagnostics));
    }

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
