using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverGitCommitProofOptOutTests
{
    // ── Commit proof artifacts opt-out ──────────────────────────────────

    [Fact]
    public async Task RunTaskAsync_WhenCommitProofArtifactsFalse_OmitsRelayProofFiles()
    {
        using var repo = TestRepository.Create();
        // Write config with commitProofArtifacts: false (WriteConfig doesn't
        // support the key, so write JSON directly).
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "test -f src/status.cs",
              "logSources": [],
              "commitProofArtifacts": false
            }
            """);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");

        // No .relay/ proof paths should appear in the commit.
        Assert.DoesNotContain(".relay/ship-status/manifest.txt", names);
        Assert.DoesNotContain(".relay/ship-status/ledger.md", names);
        Assert.DoesNotContain(".relay/ship-status/ship-status.seals", names);
        Assert.DoesNotContain(".relay/ship-status/status.json", names);

        // Code changes still committed.
        Assert.Contains("src/status.cs", names);

        // Task retirement still committed (DONE- file lands under llm-tasks/).
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));

        // Relay-Seal trailer still present.
        var message = TestGit.Run(repo.Root, "log", "-1", "--pretty=%B");
        Assert.Contains("Relay-Seal:", message);
    }

    [Fact]
    public async Task RunTaskAsync_WhenCommitProofArtifactsTrue_IncludesRelayProofFiles()
    {
        // Lock in the default: when commitProofArtifacts is explicitly true
        // (or absent), the four .relay/ proof files must be force-committed.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "test -f src/status.cs",
              "logSources": [],
              "commitProofArtifacts": true
            }
            """);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");

        // All four .relay/ proof files must be present.
        Assert.Contains(".relay/ship-status/manifest.txt", names);
        Assert.Contains(".relay/ship-status/ledger.md", names);
        Assert.Contains(".relay/ship-status/ship-status.seals", names);
        Assert.Contains(".relay/ship-status/status.json", names);

        // Code changes still committed.
        Assert.Contains("src/status.cs", names);

        // Relay-Seal trailer still present.
        var message = TestGit.Run(repo.Root, "log", "-1", "--pretty=%B");
        Assert.Contains("Relay-Seal:", message);
    }
}
