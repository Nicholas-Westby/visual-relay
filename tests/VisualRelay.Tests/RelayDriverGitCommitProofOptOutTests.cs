using System.Text.Json;
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

        // Pre-create per-stage .input.json and .report.json artifacts so
        // the commit gate can enumerate them when CommitProofArtifacts is true.
        // (The test runner doesn't write these — the real SwivalSubagentRunner does.)
        WriteStageArtifacts(repo.Root, "ship-status", stages: 9);

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

        // Per-stage artifacts must also be absent when proof artifacts are off.
        for (var s = 1; s <= 9; s++)
        {
            Assert.DoesNotContain($".relay/ship-status/stage{s}-attempt1.input.json", names);
            Assert.DoesNotContain($".relay/ship-status/stage{s}-attempt1.report.json", names);
        }

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
        // Additionally, per-stage .input.json and .report.json artifacts must
        // be included so completed stages show their actual content in the UI.
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

        // Pre-create per-stage .input.json and .report.json artifacts so
        // the commit gate can enumerate them when CommitProofArtifacts is true.
        // (The test runner doesn't write these — the real SwivalSubagentRunner does.)
        WriteStageArtifacts(repo.Root, "ship-status", stages: 9);

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

        // Per-stage .input.json and .report.json artifacts must be committed
        // so completed stages show actual content in the UI.
        for (var s = 1; s <= 9; s++)
        {
            Assert.Contains($".relay/ship-status/stage{s}-attempt1.input.json", names);
            Assert.Contains($".relay/ship-status/stage{s}-attempt1.report.json", names);
        }

        // Code changes still committed.
        Assert.Contains("src/status.cs", names);

        // Relay-Seal trailer still present.
        var message = TestGit.Run(repo.Root, "log", "-1", "--pretty=%B");
        Assert.Contains("Relay-Seal:", message);
    }

    /// <summary>
    /// Writes per-stage .input.json and .report.json artifacts (attempt 1)
    /// for stages 1..<paramref name="stages"/> under the task directory, so
    /// the commit-stage proof-file enumeration can find and force-add them.
    /// </summary>
    private static void WriteStageArtifacts(string root, string taskId, int stages)
    {
        var taskDir = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);

        for (var s = 1; s <= stages; s++)
        {
            // .input.json — matches StageInputArtifact format
            var inputPath = Path.Combine(taskDir, $"stage{s}-attempt1.input.json");
            var inputContent = JsonSerializer.Serialize(new
            {
                version = 1,
                stage = s,
                attempt = 1,
                name = $"Stage {s}",
                systemPrompt = $"System prompt for stage {s}",
                inputPrompt = $"## Task input\nStage {s} input.",
                timestamp = "2026-06-24T00:00:00Z"
            });
            File.WriteAllText(inputPath, inputContent);

            // .report.json — matches the {result:{answer:...}} wrapper
            var reportPath = Path.Combine(taskDir, $"stage{s}-attempt1.report.json");
            var reportContent = JsonSerializer.Serialize(new
            {
                result = new { answer = """{"summary":"ok"}""" }
            });
            File.WriteAllText(reportPath, reportContent);
        }
    }
}
