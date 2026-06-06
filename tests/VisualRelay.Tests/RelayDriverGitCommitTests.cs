using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverGitCommitTests
{
    [Fact]
    public async Task RunTaskAsync_WhenGitCommitEnabled_CreatesARealRelayCommit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.txt", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.txt"), "old");
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
        Assert.Contains("src/status.txt", names);
        Assert.DoesNotContain("src/ghost.txt", names);
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
        repo.WriteConfig("test -f src/status.txt", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.txt"), "old");
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
        Assert.Contains("src/status.txt", names);
    }

    [Fact]
    public async Task CommitAsync_WhenManifestDirectoryContainsDeletedFiles_StagesTheDeletions()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("test ! -e data/company.json && test ! -e data/http_log.json", []);
        repo.WriteTask("delete-data", "batch: 1\n\n# Delete stale data\n");
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

    private static string RunGit(string rootPath, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", $"-lc \"git -C '{rootPath}' {arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}

internal sealed class EditingSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.txt"), "new");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.txt","tests/status.test","src/ghost.txt"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessage":"fix(sample): ship status with a very long subject that should be cleanly truncated around a word boundary\n\n- update status output\n- keep proof files staged\nthis prose gets dropped"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

internal sealed class DeletingDirectorySubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "data.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.Delete(Path.Combine(invocation.TargetRoot, "data", "company.json"));
            File.Delete(Path.Combine(invocation.TargetRoot, "data", "http_log.json"));
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["delete"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"stale fixtures","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"delete stale data","manifest":["data","tests/data.test"]}""",
            5 => """{"testFiles":["tests/data.test"],"rationale":"red first"}""",
            6 => """{"summary":"deleted stale data"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessage":"fix(sample): remove stale data"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
