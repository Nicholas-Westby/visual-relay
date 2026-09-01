using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverGitCommitGitignoredBackstopTests
{
    [Fact]
    public async Task RunTaskAsync_WhenManifestContainsGitignoredPath_Stage11BackstopNamesThePath()
    {
        // Simulates the drop-vestigial-kimi-suffix scenario: the agent lists a
        // gitignored runtime artifact (config.local.toml) in the stage-4 manifest.
        // The test-double runner bypasses the early SandboxedStage check,
        // so the gitignored path reaches stage 11 where the GitCommitter
        // backstop must reject it with an explicit path name — not bury it
        // in raw git output.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        // Runtime artifact that PrepareAsync regenerates — gitignored. Left as a
        // real, untracked file: sim.Seed always stages, so it must NOT go through
        // it or it would stop being an ignored/untracked path.
        File.WriteAllText(Path.Combine(repo.Root, "config.local.toml"), "[runtime]\nkey = \"val\"");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Seed(repo.Root, ".gitignore", "config.local.toml\n");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new GitignoredManifestSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Flagged,
            $"Expected Flagged, got {outcome.Status}: {outcome.Reason}");
        Assert.NotNull(outcome.Reason);
        // Must name the offending path explicitly — not bury it after a git hint line.
        Assert.Contains("manifest contains gitignored", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("config.local.toml", outcome.Reason, StringComparison.Ordinal);
    }


}
