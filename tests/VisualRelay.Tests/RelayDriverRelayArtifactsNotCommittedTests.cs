using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Visual Relay's own bookkeeping never enters a task commit. The run keeps
/// writing <c>.relay/&lt;task&gt;/</c> to disk — ledger, seals, manifest, status and
/// the per-stage input/report artifacts the UI reads — but the sealed commit
/// carries only the repo's own change, so a few-line edit reads as a few-line diff.
/// </summary>
public sealed class RelayDriverRelayArtifactsNotCommittedTests
{
    [Fact]
    public async Task RunTaskAsync_RelayArtifacts_AreWrittenToDiskButNeverCommitted()
    {
        using var repo = TestRepository.Create();
        // A config left over from an older version still carries the retired
        // commitProofArtifacts key: it must load without error and change nothing.
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
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        // Per-stage artifacts the real SandboxedStage writes (the test runner does not).
        WriteStageArtifacts(repo.Root, "ship-status", stages: 9);

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var changed = sim.FilesChangedInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.DoesNotContain(changed, p => p.StartsWith(".relay/", StringComparison.Ordinal));

        // The artifacts are still on disk for the UI and for a resume.
        var taskDir = Path.Combine(repo.Root, ".relay", "ship-status");
        Assert.True(File.Exists(Path.Combine(taskDir, "ledger.md")));
        Assert.True(File.Exists(Path.Combine(taskDir, "manifest.txt")));
        Assert.True(File.Exists(Path.Combine(taskDir, "ship-status.seals")));
        Assert.True(File.Exists(Path.Combine(taskDir, "status.json")));

        // The repo's own change, the retirement, and the seal are unaffected.
        Assert.Contains("src/status.cs", changed);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
        var message = sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Message;
        Assert.Contains("Relay-Seal:", message);
    }

    /// <summary>
    /// Writes the per-stage <c>.input.json</c> and <c>.report.json</c> artifacts
    /// (attempt 1) for stages 1..<paramref name="stages"/> under the task directory.
    /// </summary>
    private static void WriteStageArtifacts(string root, string taskId, int stages)
    {
        var taskDir = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);

        for (var s = 1; s <= stages; s++)
        {
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
            File.WriteAllText(Path.Combine(taskDir, $"stage{s}-attempt1.input.json"), inputContent);

            var reportContent = JsonSerializer.Serialize(new
            {
                result = new { answer = """{"summary":"ok"}""" }
            });
            File.WriteAllText(Path.Combine(taskDir, $"stage{s}-attempt1.report.json"), reportContent);
        }
    }
}
