using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// End-to-end coverage for Research reading the repository's own instruction
/// files: the real <see cref="RelayDriver"/>, wired to a real
/// <see cref="FirstPartySubagentRunner"/> over a scripted model, is run through
/// stages 1-3 of a repo that ships an <c>AGENTS.md</c>. Stage 2's persisted
/// input must carry the heading and the file; stage 3's must not — proving
/// <c>BuildInvocation</c> computes the list for Research only, not for every
/// stage that follows it.
/// </summary>
public sealed class RepositoryInstructionsIntegrationTests
{
    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
    }

    /// <summary>The real first-party runner over a scripted model.</summary>
    private static FirstPartySubagentRunner Runner(RelayConfig config, ScriptedModelTransport transport) =>
        new(transport, config,
            new DictionaryEnvironmentAccessor { ["DEEPSEEK_API_KEY"] = "sk-test-value" },
            _ => new Sink(), retryBackoffBase: TimeSpan.Zero);

    [Fact]
    public async Task RunTaskAsync_Stage2InputCarriesTheHeadingAndFile_Stage3DoesNot()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "AGENTS.md"), "# Agents\nFollow the house rules.\n");
        repo.WriteConfig("echo test", []);
        repo.WriteTask("add-status", "# Add status\nDescribe the change.\n");

        var transport = new ScriptedModelTransport()
            .Answer("""{"summary":"framed","options":["a"]}""")
            .Answer("""{"findings":"found","constraints":[]}""")
            .Answer("""{"evidence":"none","excerpts":[],"repro":"none"}""");
        var config = await RelayConfigLoader.LoadAsync(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                Runner(config, transport),
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                new GitSimEngine()),
            new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 3));

        var outcome = await driver.RunTaskAsync(repo.Root, "add-status");

        Assert.Equal(RelayTaskOutcomeStatus.Planned, outcome.Status);

        var stage2Input = ReadPersistedInput(repo, "add-status", stage: 2);
        Assert.Contains("## Repository instructions", stage2Input.InputPrompt, StringComparison.Ordinal);
        Assert.Contains("AGENTS.md", stage2Input.InputPrompt, StringComparison.Ordinal);

        var stage3Input = ReadPersistedInput(repo, "add-status", stage: 3);
        Assert.DoesNotContain("## Repository instructions", stage3Input.InputPrompt, StringComparison.Ordinal);
    }

    private static StageInputArtifact ReadPersistedInput(TestRepository repo, string taskId, int stage)
    {
        var reportFile = repo.AttemptReportPath(taskId, stage, attempt: 1);
        var inputPath = StageInputArtifact.PathFor(reportFile);
        Assert.True(StageInputArtifact.TryRead(inputPath, out var artifact),
            $"expected a persisted stage input artifact at {inputPath}");
        return artifact!;
    }
}
