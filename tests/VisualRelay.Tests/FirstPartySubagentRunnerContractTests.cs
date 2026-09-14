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

    /// <summary>
    /// A block that no repair rule can reach: the array is never closed, and
    /// guessing the rest of it would be inventing the model's work.
    /// </summary>
    private const string Unreadable = """Here it is: { "summary": "s", "options": [ }""";

    private const string Corrected = """
        ```json
        {"summary":"s","options":["a"]}
        ```
        """;

    private static StageInvocation Invocation(string? reportFile = null) =>
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
            ReportFile: reportFile ?? string.Empty,
            MaxTurns: 5);

    private static FirstPartySubagentRunner Build(
        ScriptedModelTransport transport, InMemoryRelayEventSink relayEvents)
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["DEEPSEEK_API_KEY"] = "sk-test-value",
            ["HF_TOKEN"] = "sk-test-value",
        };
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

    /// <summary>
    /// A contract that cannot be read is worth one follow-up turn to the same
    /// session before the stage's work is thrown away. Five tasks in a 27-task
    /// run were discarded on a first read, and none of them got a second.
    /// </summary>
    [Fact]
    public async Task AnUnreadableContract_IsReAskedExactlyOnce()
    {
        var transport = new ScriptedModelTransport().Answer(Unreadable).Answer(Corrected);
        var relayEvents = new InMemoryRelayEventSink();

        var result = await Build(transport, relayEvents).RunAsync(Invocation());

        Assert.True(result.IsValid, result.Error);
        Assert.Contains("\"a\"", result.Json!, StringComparison.Ordinal);
        Assert.Equal(2, transport.Requests.Count);
        var reask = Assert.Single(relayEvents.Events, e => e.EventName == "contract_reask");
        Assert.Equal("info", reask.Level);
        Assert.Contains("not valid JSON", reask.Data!["error"], StringComparison.Ordinal);
    }

    /// <summary>
    /// The re-ask is bounded at one. A second unreadable answer flags exactly as
    /// it did before, rather than spending the stage on a conversation.
    /// </summary>
    [Fact]
    public async Task ASecondUnreadableContract_FlagsWithoutAnotherReAsk()
    {
        var transport = new ScriptedModelTransport().Answer(Unreadable).Answer(Unreadable);
        var relayEvents = new InMemoryRelayEventSink();

        var result = await Build(transport, relayEvents).RunAsync(Invocation());

        Assert.False(result.IsValid);
        Assert.Contains("not valid JSON", result.Error!, StringComparison.Ordinal);
        Assert.Equal(2, transport.Requests.Count);
    }

    /// <summary>
    /// A missing key is worth the same one turn. Measured on the Windows arm with
    /// apache/commons-lang: Plan's answer held valid JSON with "plan" and no "manifest",
    /// although the plan named both files it would change, and ten minutes of planning were
    /// flagged away without a second read.
    /// </summary>
    [Fact]
    public async Task AMissingContractKey_IsReAskedOnceNamingTheKey()
    {
        var transport = new ScriptedModelTransport()
            .Answer("""{"summary":"no options here"}""").Answer(Corrected);
        var relayEvents = new InMemoryRelayEventSink();

        var result = await Build(transport, relayEvents).RunAsync(Invocation());

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Matches(@"add (\\u0022|\\"")options(\\u0022|\\"") with the value your reply already", transport.Requests[1]);
        var reask = Assert.Single(relayEvents.Events, e => e.EventName == "contract_reask");
        Assert.Contains("missing the required key \"options\"", reask.Data!["error"], StringComparison.Ordinal);
    }

    /// <summary>A re-ask that still leaves the key out flags with the first complaint, and asks no more.</summary>
    [Fact]
    public async Task AContractStillMissingTheKeyAfterTheReAsk_FlagsWithoutAnotherTurn()
    {
        var transport = new ScriptedModelTransport()
            .Answer("""{"summary":"no options here"}""").Answer("""{"summary":"still none"}""");

        var result = await Build(transport, new InMemoryRelayEventSink()).RunAsync(Invocation());

        Assert.False(result.IsValid);
        Assert.Contains("missing the required key \"options\"", result.Error!, StringComparison.Ordinal);
        Assert.Equal(2, transport.Requests.Count);
    }

    /// <summary>
    /// The re-ask is part of the stage, so its turn and its tokens land on the
    /// stage's report. Anything else would spend money the ledger never sees.
    /// </summary>
    [Fact]
    public async Task TheReAsk_CountsTowardTheStage()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var reportFile = Path.Combine(directory, "stage1-attempt1.report.json");
        try
        {
            var transport = new ScriptedModelTransport().Answer(Unreadable).Answer(Corrected);

            await Build(transport, new InMemoryRelayEventSink())
                .RunAsync(Invocation(reportFile));

            using var document = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(reportFile));
            var stats = document.RootElement.GetProperty("stats");
            Assert.Equal(2, stats.GetProperty("llm_calls").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("timeline").GetArrayLength());
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(directory);
        }
    }
}
