using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
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

    [Fact]
    public async Task Stage4_ManifestWithMissingFile_TriggersContractRetry()
    {
        // The stage-4 manifest lists a file (src/ghost.cs) that does not exist
        // on disk. The existence check in CheckManifestAgainstGitignoreAsync
        // must trigger a corrective retry — the second attempt must return a
        // valid manifest without the missing path.
        using var repo = TestRepository.Create();
        // Set up a real git repo with existing files.
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "content");
        // src/ghost.cs is deliberately NOT created — it is a missing path.

        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-existence-retry",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            printf '%s' "$last" > "prompt-$(basename "$trace_dir").txt"
            if [[ "$trace_dir" == *attempt2* ]]; then
              printf '```json\n{"plan":"edit only existing files","manifest":["src/status.cs"]}\n```\n'
              exit 0
            else
              printf '```json\n{"plan":"edit files","manifest":["src/status.cs","src/ghost.cs"]}\n```\n'
              exit 0
            fi
            """);
        var sink = new InMemoryRelayEventSink();
        var config = ManifestExistenceRetryConfig();
        var runner = new SwivalSubagentRunner(config, script, sink, SwivalTestHelpers.AlwaysReady);

        var stage4 = RelayStages.All[3]; // stage 4 Plan
        var invocation = new StageInvocation(
            stage4, "balanced", "run-1", repo.Root, "task", "# Task",
            string.Empty, [], [],
            Path.Combine(repo.Root, ".relay", "task", "stage4-attempt1"),
            Path.Combine(repo.Root, ".relay", "task", "stage4-attempt1.report.json"),
            1);

        var result = await runner.RunAsync(invocation);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.Contains("edit only existing files", result.Json, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e => e.EventName == "contract_retry");

        // The corrective prompt must name the missing path.
        var correctivePrompt = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, "prompt-stage4-attempt2.txt"));
        Assert.Contains("src/ghost.cs", correctivePrompt, StringComparison.Ordinal);
        Assert.Contains("does not exist", correctivePrompt, StringComparison.Ordinal);
    }

    private static RelayConfig ManifestExistenceRetryConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            1, 1, 1,
            false, true,
            5_000, 300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            BypassSandbox: true,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
