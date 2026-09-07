using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Covers what the runner does between the model's answer and the driver's
/// verdict: repairing a contract block that is one punctuation error from valid,
/// and asking once for a corrected one when it is not.
/// </summary>
public sealed class FirstPartySubagentRunnerContractTests
{
    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent)
        {
            // The agent stream is not what these tests assert on; the relay sink is.
        }
    }

    private static StageInvocation Invocation() =>
        new(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: Path.GetTempPath(),
            TaskName: "a-task",
            TaskInput: "do the thing",
            LedgerSoFar: "(none)",
            Manifest: [],
            LogSources: [],
            TraceDirectory: Path.GetTempPath(),
            ReportFile: string.Empty,
            MaxTurns: 5);

    private static FirstPartySubagentRunner Build(
        ScriptedModelTransport transport, InMemoryRelayEventSink relayEvents)
    {
        var env = new DictionaryEnvironmentAccessor();
        env["DEEPSEEK_API_KEY"] = "sk-test-value";
        env["HF_TOKEN"] = "sk-test-value";
        return new FirstPartySubagentRunner(
            transport, RelayConfigLoader.Defaults(), env, _ => new Sink(),
            retryBackoffBase: TimeSpan.Zero, relayEvents: relayEvents);
    }

    /// <summary>
    /// A block whose only defect is a literal newline inside a string value is
    /// repaired and the stage goes on — but the repair is announced, because a
    /// contract that only survived repair is still a defect worth seeing.
    /// </summary>
    [Fact]
    public async Task ARepairedContract_IsAnnouncedAsAWarning()
    {
        var transport = new ScriptedModelTransport().Answer(
            "```json\n{\"summary\": \"line one\nline two\", \"options\": []}\n```");
        var relayEvents = new InMemoryRelayEventSink();

        var result = await Build(transport, relayEvents).RunAsync(Invocation());

        Assert.True(result.IsValid, result.Error);
        var repaired = Assert.Single(
            relayEvents.Events, e => e.EventName == "contract_repaired");
        Assert.Equal("warn", repaired.Level);
        Assert.Contains("control characters", repaired.Data!["repairs"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A contract that parses as written is not announced: the warning has to
    /// mean something when it does fire.
    /// </summary>
    [Fact]
    public async Task AValidContract_IsNotAnnouncedAsRepaired()
    {
        var transport = new ScriptedModelTransport().Answer("""{"summary":"s","options":[]}""");
        var relayEvents = new InMemoryRelayEventSink();

        await Build(transport, relayEvents).RunAsync(Invocation());

        Assert.DoesNotContain(relayEvents.Events, e => e.EventName == "contract_repaired");
    }
}
