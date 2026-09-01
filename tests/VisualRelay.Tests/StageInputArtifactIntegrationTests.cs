using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the stage-input artifact the runner writes beside a stage's report.
/// <para>
/// The GUI's stage-input pane reads this file, so it is the only way a user can
/// see the prompt a stage was given. The Swival runner wrote it while building
/// its argument list; when that runner was deleted the behaviour had to move to
/// the in-process loop rather than disappear with it. These tests are the guard
/// that it did.
/// </para>
/// </summary>
public sealed class StageInputArtifactIntegrationTests
{
    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
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

    private static (FirstPartySubagentRunner Runner, InMemoryRelayEventSink Relay) Build(
        ScriptedModelTransport transport)
    {
        var env = new DictionaryEnvironmentAccessor { ["DEEPSEEK_API_KEY"] = "sk-test-value" };
        var relay = new InMemoryRelayEventSink();
        return (
            new FirstPartySubagentRunner(
                transport, RelayConfigLoader.Defaults(), env, _ => new Sink(),
                retryBackoffBase: TimeSpan.Zero, relayEvents: relay),
            relay);
    }

    /// <summary>The artifact lands beside the report with the prompt in it.</summary>
    [Fact]
    public async Task RunAsync_WritesTheInputArtifactBesideTheReport()
    {
        using var repo = TestRepository.Create();
        var reportFile = Path.Combine(repo.Root, "stage1-attempt1.report.json");
        var (runner, _) = Build(new ScriptedModelTransport().Answer(
            """{"summary": "s", "options": ["a"]}"""));

        await runner.RunAsync(Invocation(repo.Root, reportFile));

        var inputPath = StageInputArtifact.PathFor(reportFile);
        Assert.True(File.Exists(inputPath), $"expected an input artifact at {inputPath}");
        Assert.True(StageInputArtifact.TryRead(inputPath, out var artifact));
        Assert.Equal(RelayStages.All[0].Number, artifact!.Stage);
        Assert.Equal(RelayStages.All[0].SystemPrompt, artifact.SystemPrompt);
        Assert.Contains("do the thing", artifact.InputPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stage_input event carries sizes and a path, not the prompt itself, so
    /// the pane can show the artifact without the event stream carrying it.
    /// </summary>
    [Fact]
    public async Task RunAsync_AnnouncesTheArtifactWithMetadataOnly()
    {
        using var repo = TestRepository.Create();
        var reportFile = Path.Combine(repo.Root, "stage1-attempt1.report.json");
        var (runner, relay) = Build(new ScriptedModelTransport().Answer(
            """{"summary": "s", "options": ["a"]}"""));

        await runner.RunAsync(Invocation(repo.Root, reportFile));

        var announced = Assert.Single(relay.Events, e => e.EventName == "stage_input");
        Assert.NotNull(announced.Data);
        Assert.Equal(StageInputArtifact.PathFor(reportFile), announced.Data!["path"]);
        Assert.True(int.Parse(announced.Data["inputBytes"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(int.Parse(announced.Data["systemBytes"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.DoesNotContain("do the thing", string.Join(" ", announced.Data.Values), StringComparison.Ordinal);
    }

    /// <summary>
    /// The artifact is written before the model is asked, so a stage that never
    /// finishes still shows what it was given.
    /// </summary>
    [Fact]
    public async Task AStageThatFails_StillLeavesItsInputArtifact()
    {
        using var repo = TestRepository.Create();
        var reportFile = Path.Combine(repo.Root, "stage1-attempt1.report.json");
        // Queue enough failures to exhaust the retry and the chain: the point is
        // that the artifact is on disk before the model is ever asked.
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < 8; i++) transport.Fails(500, "upstream is down");
        var (runner, _) = Build(transport);

        await runner.RunAsync(Invocation(repo.Root, reportFile));

        Assert.True(File.Exists(StageInputArtifact.PathFor(reportFile)));
    }
}
