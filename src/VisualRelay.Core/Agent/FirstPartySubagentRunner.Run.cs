using System.Net.Sockets;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

public sealed partial class FirstPartySubagentRunner
{
    /// <summary>
    /// Attempts beyond the first on any one model before moving down the chain.
    /// </summary>
    private const int RetriesPerRoute = 1;

    /// <summary>Runs one stage against one model in the tier's chain.</summary>
    private async Task<(AgentLoopResult Result, KillSignature? Kill, bool HardAbort)> RunOnRouteAsync(
        ProviderRoute route,
        IReadOnlyList<ChatMessage> conversation,
        StageInvocation invocation,
        IAgentEventSink events,
        CancellationToken cancellationToken)
    {
        var apiKey = _keys.Resolve(route.ApiKeyEnvVar) ?? string.Empty;
        var client = new ChatCompletionClient(_transport, route.Timeouts, _timeProvider);

        var budget = invocation.AbsoluteCeilingMs > 0
            ? TimeSpan.FromMilliseconds(invocation.AbsoluteCeilingMs)
            : route.Timeouts.Total;

        // The stall clocks come from the repository's config, per tier. The
        // watchdog is an event sink, so it sees the loop's own stream; the timer
        // in SuperviseAsync is what notices when that stream goes quiet.
        var (firstOutput, inactivity, outputSilence) =
            ResolveStallWindows(_config, invocation.Tier);
        var watchdog = new AgentWatchdog(
            firstOutput, inactivity, budget, outputSilence, _timeProvider);
        var loop = new AgentTurnLoop(
            client, _tools, new FanOutAgentEventSink(events, watchdog), _timeProvider);

        var options = new AgentLoopOptions(
            Model: route.UpstreamModel,
            Endpoint: route.Endpoint,
            Headers: route.Headers(apiKey),
            Capabilities: ProviderCapabilityCatalog.For(route.ProviderName),
            MaxTurns: invocation.MaxTurns,
            StageBudget: budget,
            // One retry per model, not three. The chain IS the redundancy: with
            // four models behind a tier, three retries each would mean sixteen
            // attempts against a provider outage before the tier gave up, and
            // the later models are on different providers anyway.
            RetryBudget: RetriesPerRoute,
            ContextWindow: route.ContextWindow,
            RetryBackoffBase: _retryBackoffBase);

        try
        {
            return await SuperviseAsync(
                watchdog,
                token => loop.RunAsync(
                    conversation, options, new ToolContext(invocation.TargetRoot, budget), token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            // A reset connection is a fault of THIS route, so it becomes an
            // ordinary error the chain can hop past. Letting it escape skipped
            // the report write and took the stage down with no evidence.
            return (
                new AgentLoopResult(
                    AgentLoopOutcome.Error, string.Empty, new AgentStats(),
                    $"the connection to {route.Alias} failed: {ex.Message}"),
                null,
                false);
        }
    }

    /// <summary>
    /// Writes the stage report. Failing to write one must never take the stage
    /// down with it: the result is already in hand, and losing the report is
    /// better than losing the work.
    /// </summary>
    /// <param name="invocation">The stage being reported on.</param>
    /// <param name="result">What the loop produced.</param>
    /// <param name="events">Where a write failure is announced.</param>
    /// <remarks>
    /// The write is deliberately NOT cancellable. It used to take the stage's
    /// own token, so cancelling a stage cancelled the write of the report
    /// describing it — losing exactly the evidence a killed stage most needs,
    /// and throwing a <see cref="TaskCanceledException"/> the catch below did
    /// not cover.
    /// </remarks>
    private async Task WriteReportAsync(
        StageInvocation invocation, AgentLoopResult result, IAgentEventSink events)
    {
        if (string.IsNullOrWhiteSpace(invocation.ReportFile)) return;

        try
        {
            var report = AgentReportWriter.Build(
                result, invocation.Tier, invocation.TaskInput, _timeProvider.GetUtcNow());
            await AgentReportWriter.WriteAsync(invocation.ReportFile, report, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            events.Publish(new AgentEvent(
                AgentEventKind.TurnFinished, _timeProvider.GetUtcNow(), result.Stats.Turns,
                Detail: "report could not be written: " + ex.Message));
        }
    }
}
