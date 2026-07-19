using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using static VisualRelay.Tests.RealGitIntegrationTests;

namespace VisualRelay.Tests;

/// <summary>
/// Opt-in (<c>VR_RUN_SLOW_INTEGRATION=1</c>) driver-level real-git end-to-end runs:
/// a full plan-then-execute of two tasks against a real repo (proving each sealed
/// commit contains ONLY its own files), and a real detached-HEAD verify worktree with
/// its git-ignored overlay. The always-on suite covers the same logic in-memory via
/// GitSim; these exercise the real binary end to end without slowing the default run.
/// </summary>
public sealed class RealGitIntegrationDriverTests
{
    // ── one full NoCommitContamination plan + execute run ────────────────────

    [Fact]
    public async Task TwoTasks_RealGit_PlanThenExecute_EachCommitContainsOnlyItsOwnFiles()
    {
        if (!Ready()) return;
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("task-a", "# Task A\n");
        repo.WriteTask("task-b", "# Task B\n");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "shared.cs"), "baseline");
        Git(repo.Root, "init");
        Git(repo.Root, "config", "user.email", "visual-relay@test.example");
        Git(repo.Root, "config", "user.name", "VR Tests");
        Git(repo.Root, "add", ".");
        Git(repo.Root, "commit", "-m", "chore: seed repo");
        var seedHash = Git(repo.Root, "rev-parse", "HEAD").Trim();

        var runnerA = new RealGitTwoFileRunner("task-a", "src/a.cs", "tests/a.tests.cs");
        var runnerB = new RealGitTwoFileRunner("task-b", "src/b.cs", "tests/b.tests.cs");
        var config = new RelayConfig(
            TasksDir: "llm-tasks", TestCommand: "dotnet test", TestFileCommand: "dotnet test {files}",
            LogSources: [], TierProfiles: new Dictionary<string, string>(), EnableFixVerify: true,
            MaxStageFailures: 3, MaxTurns: 200, BaselineVerify: true, ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000, TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>(), FirstOutputTimeoutMs: 660_000,
            MaxPlanConcurrency: 2, InactivityTimeoutMsByTier: null, InactivityTimeoutMs: 600_000);

        var planResults = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root, tasks: [("task-a", runnerA), ("task-b", runnerB)], config: config,
            testRunner: new ScriptedTestRunner(), gitInvoker: new GitInvoker(), cancellationToken: CancellationToken.None,
            environmentAccessor: new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.Combine(repo.Root, ".xdg") });
        Assert.All(planResults, r => Assert.Equal(RelayTaskOutcomeStatus.Planned, r.Outcome.Status));

        var executeOptions = new RelayDriverOptions(CreateGitCommit: true, Resume: true);
        var driverA = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerA, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), gitInvoker: new GitInvoker()),
            executeOptions);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, (await driverA.RunTaskAsync(repo.Root, "task-a")).Status);

        var driverB = new RelayDriver(
            RelayDriverDependencies.ForTests(runnerB, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink(), gitInvoker: new GitInvoker()),
            executeOptions);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, (await driverB.RunTaskAsync(repo.Root, "task-b")).Status);

        Assert.Equal("2", Git(repo.Root, "rev-list", "--count", $"{seedHash}..HEAD").Trim());
        var headFiles = Git(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/b.cs", headFiles, StringComparison.Ordinal);
        Assert.Contains(".relay/task-b/manifest.txt", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/a.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain(".relay/task-a/", headFiles, StringComparison.Ordinal);
        var parentFiles = Git(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD~1");
        Assert.Contains("src/a.cs", parentFiles, StringComparison.Ordinal);
        Assert.Contains(".relay/task-a/manifest.txt", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/b.cs", parentFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/shared.cs", headFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("src/shared.cs", parentFiles, StringComparison.Ordinal);
    }

    // ── one worktree-overlay end-to-end (real detached-HEAD + ignored overlay) ─

    [Fact]
    public async Task VerifyWorktree_RealGit_OverlaysTopLevelIgnoredDirAndFile_WithSourceContent()
    {
        if (!Ready()) return;
        var root = Path.Combine(Path.GetTempPath(), "vr-realgit-vw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = new RelayDriver(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink(), gitInvoker: new GitInvoker()));
        string? worktree = null;
        try
        {
            Git(root, "init", "-q");
            Git(root, "config", "user.email", "visual-relay@example.test");
            Git(root, "config", "user.name", "Visual Relay Tests");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "node_modules/\n.env\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked-content");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "dep"));
            await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "dep", "index.js"), "module.exports = 42;");
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "SECRET=abc");
            Git(root, "add", ".");
            Git(root, "commit", "-q", "-m", "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(root, "task-overlay", "run-overlay", CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(worktree, "tracked.txt")));
            Assert.True(Directory.Exists(Path.Combine(worktree, "node_modules")), "node_modules should be overlaid");
            Assert.True(File.Exists(Path.Combine(worktree, ".env")), ".env should be overlaid");
            Assert.Equal("module.exports = 42;", await File.ReadAllTextAsync(Path.Combine(worktree, "node_modules", "dep", "index.js")));
            Assert.Equal("SECRET=abc", await File.ReadAllTextAsync(Path.Combine(worktree, ".env")));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}

/// <summary>
/// Minimal two-file plan/execute runner for the real-git NoCommitContamination e2e:
/// authors one code file + one test file for its task so each sealed commit's contents
/// can be asserted. Kept local to this gated set so the always-on doubles stay lean.
/// </summary>
internal sealed class RealGitTwoFileRunner(string taskId, string codeFile, string testFile) : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(invocation.TargetRoot, testFile))!);
            File.WriteAllText(Path.Combine(invocation.TargetRoot, testFile), $"// red test for {taskId}");
        }
        else if (invocation.Stage.Number == 6)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(invocation.TargetRoot, codeFile))!);
            File.WriteAllText(Path.Combine(invocation.TargetRoot, codeFile), $"// impl for {taskId}");
        }

        var json = invocation.Stage.Number switch
        {
            1 => $$"""{"summary":"framed for {{taskId}}","options":["option-a"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"edit files for {{taskId}}","manifest":["{{codeFile}}","{{testFile}}"]}""",
            5 => $$"""{"testFiles":["{{testFile}}"],"rationale":"red first for {{taskId}}"}""",
            6 => $$"""{"summary":"implemented {{taskId}}"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => $$"""{"summary":"verified","commitMessages":["feat: {{taskId}} feature","chore: {{taskId}} cleanup","test: {{taskId}} coverage"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
