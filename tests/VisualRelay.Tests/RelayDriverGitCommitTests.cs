using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;
namespace VisualRelay.Tests;

public sealed partial class RelayDriverGitCommitTests
{
    [Fact]
    public async Task RunTaskAsync_WhenGitCommitEnabled_CreatesARealRelayCommit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        Assert.False(string.IsNullOrWhiteSpace(outcome.CommitSha));
        var message = RunGit(repo.Root, "log -1 --pretty=%B");
        Assert.Contains("fix(sample): ship status", message);
        Assert.Contains("Task: ship-status", message);
        Assert.Contains("Relay-Seal:", message);
        var names = RunGit(repo.Root, "show --name-only --pretty=format: HEAD");
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
        // which made stage 11 die with "paths are ignored by .gitignore" — no task
        // could ever commit. The committer must force the small proof files in so
        // the Relay-Seal stays verifiable while bulky scratch stays ignored.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), ".relay/*\n!.relay/config.json\n");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");

        var runner = new EditingSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = RunGit(repo.Root, "show --name-only --pretty=format: HEAD");
        Assert.Contains(".relay/ship-status/manifest.txt", names);
        Assert.Contains("src/status.cs", names);
    }

    [Fact]
    public async Task RunTaskAsync_WhenAnAgentCommitsMidRun_AgentCommitIsRejectedByHook()
    {
        // The pre-commit hook rejects commits lacking the RELAY_COMMIT_TOKEN during
        // an active run. The driver's stage-11 commit sets the token and gets through.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");
        var seed = RunGit(repo.Root, "rev-parse HEAD").Trim();

        // Install the project's pre-commit hook so the agent's commit is rejected.
        RepoSetup.InstallPreCommitHook(repo.Root);

        var runner = new MidRunCommittingSubagentRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        // The agent's attempt to commit at stage 8 should be rejected by the hook
        // (no RELAY_COMMIT_TOKEN). The agent ignores the failure and continues.
        Assert.True(runner.AgentCommitRejected,
            "agent's git commit should have been rejected by the pre-commit hook");
        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);

        // Only the driver's sealed commit should land on top of the seed.
        Assert.Equal("1", RunGit(repo.Root, $"rev-list --count {seed}..HEAD").Trim());

        var names = RunGit(repo.Root, "show --name-only --pretty=format: HEAD");
        Assert.Contains("src/status.cs", names);
        Assert.Contains(".relay/ship-status/manifest.txt", names);
        Assert.Contains("Relay-Seal:", RunGit(repo.Root, "log -1 --pretty=%B"));
    }

    [Fact]
    public async Task CommitAsync_WhenManifestDirectoryContainsDeletedFiles_StagesTheDeletions()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test ! -e data/company.json && test ! -e data/http_log.json", []);
        repo.WriteTask("delete-data", "batch: 1\n\n# Delete stale data\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "delete.cs"), "// remove stale data");
        Directory.CreateDirectory(Path.Combine(repo.Root, "data"));
        File.WriteAllText(Path.Combine(repo.Root, "data", "company.json"), "{}");
        File.WriteAllText(Path.Combine(repo.Root, "data", "http_log.json"), "{}");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed data\"");

        var runner = new DeletingDirectorySubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "delete-data");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = RunGit(repo.Root, "show --name-status --pretty=format: HEAD");
        Assert.Contains("D\tdata/company.json", names);
        Assert.Contains("D\tdata/http_log.json", names);
    }

    [Fact]
    public async Task RunTaskAsync_CommitMsgHookRejectsFileNames_FallsBackToLaterCandidate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");

        // Install a commit-msg hook that rejects subjects containing "foo.cs".
        InstallRejectingCommitMsgHook(repo.Root, "foo\\.cs");

        var runner = new FileNameFirstCandidateRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        // The first candidate ("fix(src): update foo.cs logic") contains foo.cs and
        // should be rejected by the hook.  The second candidate should land.
        var subject = RunGit(repo.Root, "log -1 --pretty=%s");
        Assert.Equal("fix: correct update logic", subject.Trim());
    }

    [Fact]
    public async Task RunTaskAsync_LegacyCommitMessageString_StillCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");

        var runner = new LegacyCommitMessageRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var subject = RunGit(repo.Root, "log -1 --pretty=%s");
        Assert.Equal("fix(legacy): use old field", subject.Trim());
    }

    [Fact]
    public async Task RunTaskAsync_MissingCommitMessages_CommitsViaSlugFallback()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.cs"), "old");
        RunGit(repo.Root, "init");
        RunGit(repo.Root, "config user.email visual-relay@example.test");
        RunGit(repo.Root, "config user.name \"Visual Relay Tests\"");
        RunGit(repo.Root, "add .");
        RunGit(repo.Root, "commit -m \"chore: seed repo\"");

        var runner = new NoCommitMessageRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var subject = RunGit(repo.Root, "log -1 --pretty=%s");
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
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");

        var runner = new NewTestFileNotInManifestRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "regression-cover");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var names = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/app.cs", names);
        Assert.Contains("tests/regression-tests.cs", names);
    }

    private static void InstallRejectingCommitMsgHook(string repoRoot, string rejectPattern)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "commit-msg");
        File.WriteAllText(hookPath,
            $"#!/usr/bin/env bash{Environment.NewLine}" +
            $"set -euo pipefail{Environment.NewLine}" +
            $"subject=\"$(head -n 1 \"$1\")\"{Environment.NewLine}" +
            $"if echo \"$subject\" | grep -qE '{rejectPattern}'; then{Environment.NewLine}" +
            $"  echo \"hook: subject matches rejected pattern\" >&2{Environment.NewLine}" +
            $"  exit 1{Environment.NewLine}" +
            $"fi{Environment.NewLine}" +
            $"exit 0{Environment.NewLine}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    private static string RunGit(string rootPath, string arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh", $"-c \"git -C '{rootPath}' {arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Strip DEVELOPER_DIR/SDKROOT so xcrun cannot resurrect a stale nix-store path.
        startInfo.Environment.Remove("DEVELOPER_DIR"); startInfo.Environment.Remove("SDKROOT");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
