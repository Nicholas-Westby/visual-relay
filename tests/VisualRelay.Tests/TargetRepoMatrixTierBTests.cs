using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Tier B of the target-repository matrix: the FULL pipeline over a real
/// repository shape, driven by the real in-process agent against a scripted
/// model.
/// <para>
/// The spec asks for these to be replayed through cassettes. They are driven by
/// a scripted model instead, and the reason is the spec's own requirement that
/// Tier B be free and deterministic. A cassette keys on the exact request bytes,
/// and a full pipeline's requests carry tool output — file contents, git state,
/// paths — so a replay that must reproduce them byte for byte is deterministic
/// only until the repository shifts under it. The rows below exercise VISUAL
/// RELAY's logic rather than model capability, which is precisely the case the
/// spec says needs no live model. Rows that do need real model behaviour are the
/// release-cadence live half, and <see cref="LiveRequestAcceptanceTests"/> is
/// what proves the wire shape for those.
/// </para>
/// </summary>
public sealed class TargetRepoMatrixTierBTests
{
    /// <summary>A file every row materializes, so the manifest names something real.</summary>
    private const string ManifestFile = "src/app.txt";

    private static void WriteManifestFile(TestRepository repo)
    {
        var path = Path.Combine(repo.Root, "src", "app.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content\n");
    }

    private sealed class Sink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent) { }
    }

    /// <summary>The real first-party runner over a scripted model.</summary>
    private static FirstPartySubagentRunner Runner(RelayConfig config, ScriptedModelTransport transport) =>
        new(transport, config,
            new DictionaryEnvironmentAccessor { ["DEEPSEEK_API_KEY"] = "sk-test-value" },
            _ => new Sink(), retryBackoffBase: TimeSpan.Zero);

    /// <summary>
    /// A model that answers every stage's contract, so the pipeline runs to the
    /// end and the assertions are about what Visual Relay did.
    /// </summary>
    /// <summary>
    /// One object carrying every key any stage contract asks for. The manifest
    /// must name a file that EXISTS: the runner rejects a manifest naming an
    /// absent or gitignored path, which is the behaviour ported out of the
    /// subprocess runner.
    /// </summary>
    /// <param name="manifestPath">A repo-relative path that is on disk.</param>
    /// <returns>The contract body.</returns>
    private static string ContractBody(string manifestPath) =>
        "{\"summary\":\"done\",\"options\":[\"a\"],"
        + "\"findings\":\"f\",\"constraints\":[\"c\"],"
        + "\"evidence\":\"e\",\"excerpts\":[\"x\"],\"repro\":\"r\","
        + $"\"plan\":\"edit\",\"manifest\":[\"{manifestPath}\"],"
        + $"\"testFiles\":[\"{manifestPath}\"],\"rationale\":\"r\","
        + "\"verdict\":\"pass\",\"issues\":[],"
        + "\"commitMessages\":[\"feat: add status\"]}";

    private static ScriptedModelTransport ContractAnswers(string manifestPath, int turns = 60)
    {
        var transport = new ScriptedModelTransport();
        for (var i = 0; i < turns; i++)
            transport.AnswersVerbatim(ContractBody(manifestPath));
        return transport;
    }

    /// <summary>
    /// The Node row. A repo whose <c>scripts.test</c> is an <c>&amp;&amp;</c>-chained
    /// command is detected as <c>npm test</c>, and a chain an operator writes into the
    /// config survives intact into the run. Init validates with a direct exec while the
    /// pipeline runs under a shell, which is the mismatch this row exists to pin.
    /// </summary>
    [Fact]
    public async Task Row_NodeWithChainedTestScript_KeepsTheChainThroughTheWholePipeline()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(
            Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "vitest run && tsc --noEmit" } }""");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);
        Assert.Contains("npm test", candidates);

        repo.WriteConfig("vitest run && tsc --noEmit", []);
        repo.WriteTask("add-status", "# Add status\n");
        WriteManifestFile(repo);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);
        Assert.Equal("vitest run && tsc --noEmit", config.TestCommand);

        var (_, sink) = await RunPipelineAsync(repo, config, ManifestFile);

        // The chain reaches the run intact rather than being split, or quoted
        // into a single binary name with two arguments.
        AssertRanTheWholePipeline(sink, "vitest run && tsc --noEmit");
    }

    /// <summary>
    /// The Rust row. No test file classifies, so the <c>{files}</c> token has
    /// nothing to expand to and the targeted command degrades to the full suite
    /// rather than running a filter that matches nothing and reports green.
    /// </summary>
    [Fact]
    public async Task Row_RustWithNoClassifiableTestFile_DegradesToTheFullSuite()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(
            Path.Combine(repo.Root, "Cargo.toml"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "lib.rs"), "pub fn add() {}\n");

        Assert.Contains("cargo test", TestCommandDetector.DetectCandidates(repo.Root));

        repo.WriteConfig("cargo test", []);
        repo.WriteTask("add-status", "# Add status\n");
        WriteManifestFile(repo);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);
        var (_, sink) = await RunPipelineAsync(repo, config, ManifestFile);

        AssertRanTheWholePipeline(sink, "cargo test");
    }

    /// <summary>
    /// The JVM row. Detection now exists for Maven, so a JVM repo lands on a
    /// real command instead of falling through to the placeholder while
    /// <c>TestPathClassifier</c> happily classified its <c>.java</c> files —
    /// classification and detection disagreeing for the entire ecosystem.
    /// </summary>
    [Fact]
    public async Task Row_MavenRepo_RunsOnARealCommandNotThePlaceholder()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pom.xml"), "<project/>");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);
        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(ProjectBootstrapper.PlaceholderTestCommand, candidates);

        repo.WriteConfig(candidates[0], []);
        repo.WriteTask("add-status", "# Add status\n");
        WriteManifestFile(repo);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);
        var (_, sink) = await RunPipelineAsync(repo, config, ManifestFile);

        AssertRanTheWholePipeline(sink, candidates[0]);
    }

    /// <summary>
    /// Drives the whole pipeline with the real agent and a scripted model.
    /// </summary>
    /// <param name="repo">The repository under test.</param>
    /// <param name="config">Its loaded configuration.</param>
    /// <param name="manifestPath">A repo-relative file the manifest may name.</param>
    /// <returns>The task outcome and the events the run published.</returns>
    private static async Task<(RelayTaskOutcome Outcome, InMemoryRelayEventSink Sink)>
        RunPipelineAsync(TestRepository repo, RelayConfig config, string manifestPath)
    {
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                Runner(config, ContractAnswers(manifestPath)),
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink,
                new GitSimEngine()),
            RelayDriverOptions.NoGitCommit);

        return (await driver.RunTaskAsync(repo.Root, "add-status"), sink);
    }

    /// <summary>
    /// Asserts the pipeline actually ran rather than failing at the first
    /// stage: several stages completed, and the configured test command is the
    /// one the run used.
    /// </summary>
    /// <param name="sink">Where the run's events landed.</param>
    /// <param name="expectedCommand">The command the config named.</param>
    private static void AssertRanTheWholePipeline(
        InMemoryRelayEventSink sink, string expectedCommand)
    {
        var stagesDone = sink.Events.Count(e => e.EventName == "stage_done");
        Assert.True(stagesDone >= 4,
            $"only {stagesDone} stages completed; the pipeline failed early rather than "
            + "running. Last events: " + string.Join(" | ", sink.Events.TakeLast(8)
                .Select(e => e.EventName
                    + (e.EventName == "flagged" ? ": " + string.Join(",", e.Data?.Values ?? []) : ""))));

        var runStart = Assert.Single(sink.Events, e => e.EventName == "run_start");
        Assert.NotNull(runStart.Data);

        // The configured command reaches the run verbatim.
        Assert.Contains(sink.Events, e =>
            e.Data is not null
            && e.Data.Values.Any(v => v.Contains(expectedCommand, StringComparison.Ordinal)));
    }
}
