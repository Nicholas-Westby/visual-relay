using System.Net.Sockets;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Llm;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Covers what a stage leaves behind when it does NOT finish cleanly.
/// <para>
/// These are the paths an audit found unguarded: a cancelled stage lost its
/// report because the write took the very token that cancelled it, a watchdog
/// kill reached the driver stripped of the signature it branches on, and a
/// reset connection escaped every catch and took the stage down with no
/// evidence at all.
/// </para>
/// </summary>
public sealed class FirstPartySubagentRunnerFaultTests
{
    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
    }

    private sealed class ThrowingTransport(Exception fault) : IProviderTransport
    {
        public Task<ProviderResponse> SendAsync(
            ProviderRequest request, CancellationToken cancellationToken = default) =>
            throw fault;

        public Task<ProviderStreamResponse> StreamAsync(
            ProviderRequest request, CancellationToken cancellationToken = default) =>
            throw fault;
    }

    private static StageInvocation Invocation(string root, string reportFile) =>
        new(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: root,
            TaskName: "a-task",
            TaskInput: "do the thing",
            LedgerSoFar: "(none)",
            Manifest: [],
            LogSources: [],
            TraceDirectory: root,
            ReportFile: reportFile,
            MaxTurns: 4);

    private static FirstPartySubagentRunner Build(IProviderTransport transport) =>
        new(transport, RelayConfigLoader.Defaults(),
            new DictionaryEnvironmentAccessor { ["DEEPSEEK_API_KEY"] = "sk-test-value" },
            _ => new Sink(), retryBackoffBase: TimeSpan.Zero);

    /// <summary>
    /// A cancelled stage still leaves its report. The write used to be handed
    /// the stage's own token, so cancelling the stage cancelled the write of the
    /// report describing it.
    /// </summary>
    [Fact]
    public async Task ACancelledStage_StillLeavesItsReport()
    {
        using var repo = TestRepository.Create();
        var reportFile = Path.Combine(repo.Root, "stage1-attempt1.report.json");
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 8; i++) transport.Answer("thinking about it");
        using var cts = new CancellationTokenSource();

        var runner = Build(transport);
        var run = runner.RunAsync(Invocation(repo.Root, reportFile), cts.Token);
        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.True(File.Exists(reportFile), "a cancelled stage must still leave its report");
    }

    /// <summary>
    /// A reset connection becomes an ordinary error the chain can hop past, and
    /// the report is written. It used to escape every catch.
    /// </summary>
    /// <param name="fault">A transport-level failure.</param>
    [Theory]
    [MemberData(nameof(TransportFaults))]
    public async Task ATransportFault_IsReportedRatherThanThrown(Exception fault)
    {
        using var repo = TestRepository.Create();
        var reportFile = Path.Combine(repo.Root, "stage1-attempt1.report.json");

        var result = await Build(new ThrowingTransport(fault))
            .RunAsync(Invocation(repo.Root, reportFile));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(reportFile), "a transport fault must still leave a report");
    }

    /// <summary>
    /// A watchdog kill reaches the driver WITH its signature, and hard aborts
    /// are marked as such. The driver branches on both to decide flag-now versus
    /// escalate-and-retry; the runner used to discard them, so a ceiling kill
    /// looked like an ordinary failure a dearer tier could fix.
    /// </summary>
    /// <param name="outcome">A watchdog outcome.</param>
    /// <param name="expectHardAbort">Whether the driver must not escalate it.</param>
    [Theory]
    [InlineData(AgentWatchdogOutcome.FiredAbsoluteCeiling, true)]
    [InlineData(AgentWatchdogOutcome.FiredOutputSilence, true)]
    [InlineData(AgentWatchdogOutcome.FiredSocketWedge, true)]
    [InlineData(AgentWatchdogOutcome.FiredStall, false)]
    public void AHostCondition_IsAHardAbortAndAStallIsNot(
        AgentWatchdogOutcome outcome, bool expectHardAbort)
    {
        Assert.Equal(expectHardAbort, AgentWatchdog.IsHardAbort(outcome));
    }

    /// <summary>The transport failures a provider call can raise.</summary>
    /// <returns>One failure per row.</returns>
    public static TheoryData<Exception> TransportFaults() =>
    [
        new HttpRequestException("connection reset by peer"),
        new IOException("the response ended prematurely"),
        new SocketException(104),
    ];
}
