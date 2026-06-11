using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Gitignored-manifest backstop test, split out of RelayDriverGitCommitTests.cs to
// keep each file under the 300-line guard. Uses helpers from the main partial class.
public sealed partial class RelayDriverGitCommitTests
{
    [Fact]
    public async Task RunTaskAsync_WhenManifestContainsGitignoredPath_Stage11BackstopNamesThePath()
    {
        // Simulates the drop-vestigial-kimi-suffix scenario: the agent lists a
        // gitignored runtime artifact (swival.toml) in the stage-4 manifest.
        // The test-double runner bypasses the early SwivalSubagentRunner check,
        // so the gitignored path reaches stage 11 where the GitCommitter
        // backstop must reject it with an explicit path name — not bury it
        // in raw git output.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        // Runtime artifact that PrepareAsync regenerates — gitignored.
        File.WriteAllText(Path.Combine(repo.Root, "swival.toml"), "[runtime]\nkey = \"val\"");
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), "swival.toml\n");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");

        var runner = new GitignoredManifestSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Flagged,
            $"Expected Flagged, got {outcome.Status}: {outcome.Reason}");
        Assert.NotNull(outcome.Reason);
        // Must name the offending path explicitly — not bury it after a git hint line.
        Assert.Contains("manifest contains gitignored", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("swival.toml", outcome.Reason, StringComparison.Ordinal);
    }
}
