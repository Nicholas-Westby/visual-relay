using System.Text.RegularExpressions;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using VisualRelay.GitSim;

namespace VisualRelay.Tests;

public sealed class RelayDriverGitCommitTests
{
    [Fact]
    public async Task RunTaskAsync_WhenGitCommitEnabled_CreatesARealRelayCommit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        Assert.False(string.IsNullOrWhiteSpace(outcome.CommitSha));
        var message = sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Message;
        Assert.Contains("fix(sample): ship status", message);
        Assert.Contains("Task: ship-status", message);
        Assert.Contains("Relay-Seal:", message);
        var names = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains(".relay/ship-status/manifest.txt", names);
        Assert.Contains("src/status.cs", names);
        Assert.DoesNotContain("src/ghost.cs", names);
        Assert.True(File.Exists(Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2", "DONE-ship-status.md")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "ship-status.md")));
    }

    [Fact]
    public async Task RunTaskAsync_WhenRelayDirIsGitignored_StillCommitsTheProofFiles()
    {
        // The self-hosting repo gitignores .relay/* (run scratch — report.json,
        // run.log — is bulky), keeping only config.json. The commit's proof files
        // (ledger/seals/manifest) live under .relay/<task>/ and so are ignored too,
        // which made stage 12 die with "paths are ignored by .gitignore" — no task
        // could ever commit. The committer must force the small proof files in so
        // the Relay-Seal stays verifiable while bulky scratch stays ignored.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Seed(repo.Root, ".gitignore", ".relay/*\n!.relay/config.json\n");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains(".relay/ship-status/manifest.txt", names);
        Assert.Contains("src/status.cs", names);
    }

    [Fact]
    public async Task RunTaskAsync_WhenAnAgentCommitsMidRun_AgentCommitIsRejectedByHook()
    {
        SlowIntegration.SkipIfNotOptedIn();

        // The pre-commit hook rejects commits lacking the RELAY_COMMIT_TOKEN during
        // an active run. The driver's stage-12 commit sets the token and gets through.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "init");
        RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "config user.email visual-relay@example.test");
        RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "add .");
        RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "commit -m \"chore: seed repo\"");
        var seed = RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Install the project's pre-commit hook so the agent's commit is rejected.
        RepoSetup.InstallPreCommitHook(repo.Root);

        var runner = new MidRunCommittingSubagentRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        // The agent's attempt to commit at stage 9 should be rejected by the hook
        // (no RELAY_COMMIT_TOKEN). The agent ignores the failure and continues.
        Assert.True(runner.AgentCommitRejected,
            "agent's git commit should have been rejected by the pre-commit hook");
        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);

        // Only the driver's sealed commit should land on top of the seed.
        Assert.Equal("1", RelayDriverGitCommitTestHelpers.RunGit(repo.Root, $"rev-list --count {seed}..HEAD").Trim());

        var names = RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "show --name-only --pretty=format: HEAD");
        Assert.Contains("src/status.cs", names);
        Assert.Contains(".relay/ship-status/manifest.txt", names);
        Assert.Contains("Relay-Seal:", RelayDriverGitCommitTestHelpers.RunGit(repo.Root, "log -1 --pretty=%B"));
    }

    [Fact]
    public async Task CommitAsync_WhenManifestDirectoryContainsDeletedFiles_StagesTheDeletions()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test ! -e data/company.json && test ! -e data/http_log.json", []);
        repo.WriteTask("delete-data", "batch: 1\n\n# Delete stale data\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/delete.cs", "// remove stale data");
        sim.Seed(repo.Root, "data/company.json", "{}");
        sim.Seed(repo.Root, "data/http_log.json", "{}");
        sim.Commit(repo.Root, "chore: seed data");

        var runner = new DeletingDirectorySubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "delete-data");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var head = sim.Head(repo.Root)!;
        var parent = sim.CommitInfo(repo.Root, head)!.Parents[0];
        var parentFiles = sim.FilesInCommit(repo.Root, parent);
        var headFiles = sim.FilesInCommit(repo.Root, head);
        Assert.Contains("data/company.json", parentFiles);
        Assert.DoesNotContain("data/company.json", headFiles);
        Assert.Contains("data/http_log.json", parentFiles);
        Assert.DoesNotContain("data/http_log.json", headFiles);
    }

    [Fact]
    public async Task RunTaskAsync_CommitMsgHookRejectsFileNames_FallsBackToLaterCandidate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        // Install a commit-msg hook that rejects subjects containing "foo.cs".
        sim.PreCommitHook = req => Regex.IsMatch(req.Message.Split('\n')[0], "foo\\.cs")
            ? GitSimHookVerdict.Reject("hook: subject matches rejected pattern")
            : GitSimHookVerdict.Accept;

        var runner = new FileNameFirstCandidateRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        // The first candidate ("fix(src): update foo.cs logic") contains foo.cs and
        // should be rejected by the hook.  The second candidate should land.
        var subject = sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Message.Split('\n')[0];
        Assert.Equal("fix: correct update logic", subject.Trim());
    }

    [Fact]
    public async Task RunTaskAsync_LegacyCommitMessageString_StillCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new LegacyCommitMessageRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var subject = sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Message.Split('\n')[0];
        Assert.Equal("fix(legacy): use old field", subject.Trim());
    }

    [Fact]
    public async Task RunTaskAsync_MissingCommitMessages_CommitsViaSlugFallback()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new NoCommitMessageRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var subject = sim.CommitInfo(repo.Root, sim.Head(repo.Root)!)!.Message.Split('\n')[0];
        Assert.Equal("chore(relay): ship-status", subject.Trim());
    }

    [Fact]
    public async Task RunTaskAsync_CommitsNewTestFileNotListedInManifest()
    {
        // A new test file authored during stage 5 that the stage-4 manifest never
        // listed must land in the commit — never silently dropped. Stage 9 verifies
        // the working tree (which has the file), so the commit must match.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/app.cs", []);
        repo.WriteTask("regression-cover", "batch: 3\n\n# Add regression coverage\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        var runner = new NewTestFileNotInManifestRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "regression-cover");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains("src/app.cs", names);
        Assert.Contains("tests/regression-tests.cs", names);
    }
}
