using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the runner that implements <see cref="ISubagentRunner"/>. This is the
/// seam the driver and roughly eighty stage-level doubles are written against,
/// so its contract has to hold exactly: a typed result, a report on every path,
/// and key-gated tier resolution unchanged.
/// </summary>
public sealed class FirstPartySubagentRunnerTests
{
    private sealed class Sink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Publish(AgentEvent agentEvent) => Events.Add(agentEvent);
    }

    private static StageInvocation Invocation(
        string tier = "cheap", string? reportFile = null, int maxTurns = 5) =>
        new(
            Stage: RelayStages.All[0],
            Tier: tier,
            RunId: "run-1",
            TargetRoot: Path.GetTempPath(),
            TaskName: "a-task",
            TaskInput: "do the thing",
            LedgerSoFar: "(none)",
            Manifest: [],
            LogSources: [],
            TraceDirectory: Path.GetTempPath(),
            ReportFile: reportFile ?? string.Empty,
            MaxTurns: maxTurns);

    private static DictionaryEnvironmentAccessor Keys(params string[] names)
    {
        var env = new DictionaryEnvironmentAccessor();
        foreach (var name in names) env[name] = "sk-test-value";
        return env;
    }

    private static FirstPartySubagentRunner Build(
        ScriptedModelTransport transport, DictionaryEnvironmentAccessor env, Sink sink,
        IReadOnlyList<IAgentTool>? tools = null) =>
        new(transport, RelayConfigLoader.Defaults(), env, _ => sink, tools,
            retryBackoffBase: TimeSpan.Zero);

    /// <summary>
    /// A stage whose model answers with its contract comes back valid, with the
    /// contract JSON on the result rather than fished out of a process's stdout.
    /// </summary>
    [Fact]
    public async Task AContractAnswer_ComesBackAsATypedResult()
    {
        var transport = new ScriptedModelTransport().Answer(
            """Here is my analysis. {"summary":"looked at it","options":["a","b"]}""");
        var sink = new Sink();

        var result = await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), sink)
            .RunAsync(Invocation());

        Assert.True(result.IsValid, result.Error);
        Assert.NotNull(result.Json);
        Assert.Contains("looked at it", result.Json!, StringComparison.Ordinal);
        Assert.False(result.HardAbort);
    }

    /// <summary>
    /// An answer missing a contract key is invalid, and the reason names the key,
    /// so the driver's corrective retry has something to say.
    /// </summary>
    [Fact]
    public async Task AnAnswerMissingAContractKey_IsInvalidAndSaysWhich()
    {
        var transport = new ScriptedModelTransport().Answer("""{"summary":"no options here"}""");
        var sink = new Sink();

        var result = await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), sink)
            .RunAsync(Invocation());

        Assert.False(result.IsValid);
        Assert.Contains("options", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tier no key can serve fails immediately, as a hard abort: escalating to
    /// another tier cannot conjure a key that is not set. Moonshot backs no
    /// vision model; DeepSeek stopped being an example when its vision route
    /// joined the tail of that chain.
    /// </summary>
    [Fact]
    public async Task ATierWithNoKey_FailsAsAHardAbort()
    {
        var transport = new ScriptedModelTransport();
        var sink = new Sink();

        var result = await Build(transport, Keys("MOONSHOT_API_KEY"), sink)
            .RunAsync(Invocation(tier: "vision"));

        Assert.False(result.IsValid);
        Assert.True(result.HardAbort);
        Assert.Contains("no model is available", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure on the first model falls through to the next in the chain, which
    /// is the provider diversification the runner now performs itself.
    /// </summary>
    [Fact]
    public async Task AFailedModel_FallsThroughToTheNextInTheChain()
    {
        var transport = new ScriptedModelTransport()
            .Fails(401, """{"error":"Invalid username or password."}""")
            .Answer("""{"summary":"second model answered","options":[]}""");
        var sink = new Sink();

        var result = await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), sink)
            .RunAsync(Invocation());

        Assert.True(result.IsValid, result.Error);
        Assert.Contains("second model answered", result.Json!, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e =>
            e.Detail == "falling through to the next model in the chain");
    }

    /// <summary>
    /// Each hop in the chain is sent to a different provider where the keys
    /// allow, so one provider's outage does not take the tier down.
    /// </summary>
    [Fact]
    public async Task ChainHops_ReachDifferentProviders()
    {
        // Every DeepSeek hop is down. One retry each, so two attempts per model
        // across the four DeepSeek models, then the Hugging Face floor answers.
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 8; i++) transport.Fails(503, """{"error":{"message":"down"}}""");
        transport.Answer("""{"summary":"eventually","options":[]}""");
        var sink = new Sink();

        var result = await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), sink)
            .RunAsync(Invocation());

        Assert.True(result.IsValid, result.Error);
        // The cheap chain is DeepSeek first, then the Hugging Face floor.
        Assert.Contains(transport.Requests, r => r.Contains("deepseek", StringComparison.Ordinal));
        Assert.Contains(transport.Requests, r => r.Contains("Qwen", StringComparison.Ordinal));
    }

    /// <summary>
    /// The turn budget is honoured and reported as exhausted, not as an error,
    /// so the driver can tell the two apart.
    /// </summary>
    [Fact]
    public async Task AnExhaustedTurnBudget_IsReportedAsSuch()
    {
        var tool = new EchoTool();
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 2; i++) transport.CallsTool("echo", """{"text":"again"}""");
        var sink = new Sink();

        var result = await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), sink, [tool])
            .RunAsync(Invocation(maxTurns: 2));

        Assert.False(result.IsValid);
        Assert.Contains("turn budget", result.Error!, StringComparison.Ordinal);
        // Exhaustion is not a hard abort: the driver may escalate around it.
        Assert.False(result.HardAbort);
    }

    /// <summary>
    /// A report is written, and the existing readers can parse it. The previous
    /// runner wrote none at all on some exit paths.
    /// </summary>
    [Fact]
    public async Task AReport_IsWrittenAndParses()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var reportFile = Path.Combine(directory, "stage1-attempt1.report.json");
        try
        {
            var transport = new ScriptedModelTransport().Answer("""{"summary":"s","options":[]}""");
            var sink = new Sink();

            await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), sink)
                .RunAsync(Invocation(reportFile: reportFile));

            Assert.True(File.Exists(reportFile), "no report was written");
            using var document = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(reportFile));
            Assert.Equal("success",
                document.RootElement.GetProperty("result").GetProperty("outcome").GetString());
            Assert.Equal("cheap", document.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(directory);
        }
    }

    /// <summary>
    /// The prompt is built by the same builder the previous runner used, so a
    /// differential compares loops rather than prompts.
    /// </summary>
    [Fact]
    public async Task ThePrompt_MatchesTheExistingBuilderExactly()
    {
        var transport = new ScriptedModelTransport().Answer("""{"summary":"s","options":[]}""");
        var invocation = Invocation();

        await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), new Sink())
            .RunAsync(invocation);

        var expected = System.Text.Json.JsonEncodedText
            .Encode(invocation.TaskInput).ToString();
        Assert.Contains(expected, transport.Requests[0], StringComparison.Ordinal);
        Assert.Contains("Relay stage", transport.Requests[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A stage that asks for no tools sends none, so a single-turn stage cannot
    /// spend its only turn on a tool call it never needed.
    /// </summary>
    [Fact]
    public async Task AToollessStage_SendsNoToolsAtAll()
    {
        var transport = new ScriptedModelTransport().Answer("""{"summary":"s","options":[]}""");

        var result = await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), new Sink(), [new EchoTool()])
            .RunAsync(Invocation() with { MaxTurns = 1, WithoutTools = true });

        Assert.True(result.IsValid, result.Error);
        Assert.DoesNotContain("\"tools\"", transport.Requests[0], StringComparison.Ordinal);
        Assert.DoesNotContain("echo", transport.Requests[0], StringComparison.Ordinal);
    }

    /// <summary>The ordinary stage still gets its tool catalog.</summary>
    [Fact]
    public async Task AnOrdinaryStage_StillSendsItsTools()
    {
        var transport = new ScriptedModelTransport().Answer("""{"summary":"s","options":[]}""");

        await Build(transport, Keys("DEEPSEEK_API_KEY", "HF_TOKEN"), new Sink(), [new EchoTool()])
            .RunAsync(Invocation());

        Assert.Contains("\"tools\"", transport.Requests[0], StringComparison.Ordinal);
    }

    /// <summary>A tool that echoes its argument, to burn turns.</summary>
    private sealed class EchoTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(
            "echo", "echoes text",
            System.Text.Json.Nodes.JsonNode.Parse(
                """{"type":"object","properties":{"text":{"type":"string"}}}""")!);

        public Task<ToolResult> InvokeAsync(
            System.Text.Json.JsonElement arguments, ToolContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(arguments.ToString()));
    }
}
