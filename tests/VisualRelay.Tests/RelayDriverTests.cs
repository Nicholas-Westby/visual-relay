using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverTests
{
    [Fact]
    public async Task RunTaskAsync_WritesLedgerSealsManifestAndStructuredEvents()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("add-status", "# Add status\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "ACTIVE")));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "add-status", "ledger.md")));
        Assert.Equal(
            $"src/status.cs{Environment.NewLine}tests/status.tests.cs{Environment.NewLine}",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "add-status", "manifest.txt")));

        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "add-status", "add-status.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":5", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
        Assert.Contains(sink.Events, e => e.EventName == "stage_start" && e.StageNumber == 1);
        Assert.Contains(sink.Events, e => e.EventName == "stage_done" && e.StageNumber == 11);
        var stage11Done = sink.Events.Single(e => e.EventName == "stage_done" && e.StageNumber == 11);
        Assert.False(stage11Done.Data?.ContainsKey("turns"));
        Assert.Contains(sink.Events, e => e.EventName == "run_start" && e.Data is not null && e.Data["base_url"] == ModelBackend.BaseUrl);
    }

    [Fact]
    public async Task RunTaskAsync_StripsPrematureImplementationBeforeAuthorTestGate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", []);
        repo.WriteTask("repair-status", "# Repair status\n");
        InitGitRepo(repo.Root);
        var testRunner = new RedGateObservingTestRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "repair-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(["old", "new"], testRunner.StatusSnapshots);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repo.Root, "src", "status.cs")));
        Assert.DoesNotContain("relay-redgate:repair-status:", TestGit.Run(repo.Root, "stash", "list"));
    }

    [Fact]
    public async Task RunTaskAsync_NonCodeOnlyChange_SkipsRedGateAndCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("update-readme", "# Update README\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "docs"));
        File.WriteAllText(Path.Combine(repo.Root, "docs", "README.md"), "# Title\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedNonCodeOnly("docs/README.md");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "update-readme");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task RunTaskAsync_FlagsUnexpectedRunnerCrashAndSkipsFuturePendingLists()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("crashy", "# Crashy\n");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new ThrowingSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "crashy");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "crashy", "NEEDS-REVIEW"));
        Assert.Contains("exception: kaboom", review, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingSubagentRunner), review, StringComparison.Ordinal);
        Assert.Empty(await new RelayTaskRepository(repo.Root).ListPendingAsync());
    }

    [Fact]
    public async Task RunTaskAsync_AllocatesNextAttemptIndexOnEachReRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("re-run", "# Re-run\n");

        await RunHappyPath(repo, "re-run");
        await RunHappyPath(repo, "re-run");
        await RunHappyPath(repo, "re-run");

        var taskDirectory = Path.Combine(repo.Root, ".relay", "re-run");
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt1.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt2.report.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "stage1-attempt3.report.json")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt1")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt2")));
        Assert.True(Directory.Exists(Path.Combine(taskDirectory, "stage1-attempt3")));
    }

    [Fact]
    public async Task RunTaskAsync_EmitsTurnsKeyWhenEstimateHasTurns()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("turns-task", "# Turns task\n");
        var runner = new TurnsReportingSubagentRunner(3);
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "turns-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage1Done = sink.Events.Single(e => e.EventName == "stage_done" && e.StageNumber == 1);
        Assert.NotNull(stage1Done.Data);
        Assert.True(stage1Done.Data!.ContainsKey("turns"));
        Assert.Equal("3", stage1Done.Data["turns"]);
        var stage11Done = sink.Events.Single(e => e.EventName == "stage_done" && e.StageNumber == 11);
        Assert.False(stage11Done.Data?.ContainsKey("turns"));
    }

    private static async Task RunHappyPath(TestRepository repo, string taskId)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, (await driver.RunTaskAsync(repo.Root, taskId)).Status);
    }

    [Fact]
    public async Task RunTaskAsync_RetriesTaskThatWasAlreadyNeedsReview()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("retry-me", "# Retry\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "retry-me"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "retry-me", "NEEDS-REVIEW"), "old failure\n");
        var runner = new ScriptedSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "retry-me");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "retry-me", "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task RunTaskAsync_ManifestContainingTasksDirPath_DropsEntriesAndProceeds()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-manifest", "# Bad manifest\n");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new BadManifestSubagentRunner(), tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-manifest");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain("manifest may not include task files", outcome.Reason ?? "", StringComparison.Ordinal);

        var manifestContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "bad-manifest", "manifest.txt"));
        Assert.Contains("src/real.cs", manifestContent, StringComparison.Ordinal);
        Assert.DoesNotContain("llm-tasks/", manifestContent, StringComparison.Ordinal);

        var ledgerContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "bad-manifest", "ledger.md"));
        Assert.Contains("> **Note**: dropped 1 task-dir entry from manifest: `llm-tasks/extra.md`", ledgerContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ManifestWithOnlyTaskDirEntries_DropsAllAndProceeds()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("only-task-dir", "# Only task dir\n");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new OnlyTaskDirManifestSubagentRunner(), new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "only-task-dir");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain("manifest may not include task files", outcome.Reason ?? "", StringComparison.Ordinal);

        var manifestContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "only-task-dir", "manifest.txt"));
        // Empty manifest: only the trailing newline written by WriteManifestAsync
        Assert.Equal(Environment.NewLine, manifestContent);

        var ledgerContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "only-task-dir", "ledger.md"));
        Assert.Contains("> **Note**: dropped 2 task-dir entries from manifest: `llm-tasks/a.md`, `llm-tasks/b.md`", ledgerContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ArrayRootJson_FlagsCleanlyWithoutException()
    {
        // When a buggy ISubagentRunner returns IsValid=true with array-root JSON
        // (bypassing the extractor's object-root guard), the driver must detect
        // the invalid shape and flag cleanly — never throw an unhandled exception
        // that produces an "exception:" NEEDS-REVIEW.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("array-root", "# Array root\n");
        var runner = new ArrayRootSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "array-root");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "array-root", "NEEDS-REVIEW"));
        Assert.DoesNotContain("exception:", review, StringComparison.Ordinal);
        // The error must describe the shape problem, not a raw InvalidOperationException.
        Assert.True(
            review.Contains("object", StringComparison.OrdinalIgnoreCase) ||
            review.Contains("shape", StringComparison.OrdinalIgnoreCase) ||
            review.Contains("array", StringComparison.OrdinalIgnoreCase),
            $"NEEDS-REVIEW must describe the shape problem; got: {review}");
    }

    [Fact]
    public async Task BaselineVerify_True_PreExistingFailure_DoesNotFlag()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true);
        repo.WriteTask("pre-existing-fail", "# Pre-existing failure\n");
        InitGitRepo(repo.Root);

        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "Failed OldTest"),   // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed OldTest"),   // stage 9 verify working — same failure
            new TestRunResult(1, "Failed OldTest"));  // stage 9 verify baseline — same failure
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "pre-existing-fail");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task BaselineVerify_True_NewFailure_FlagsWithNewFailures()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true, maxVerifyLoops: 0);
        repo.WriteTask("new-failure", "# New failure\n");
        InitGitRepo(repo.Root);

        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed OldTest\nFailed NewTest"),         // stage 9 working — OldTest + NewTest
            new TestRunResult(1, "Failed OldTest"));                        // stage 9 baseline — only OldTest
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "new-failure");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("new test failures", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("NewTest", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaselineVerify_False_AnyFailure_FlagsImmediately()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 0);
        repo.WriteTask("any-failure", "# Any failure\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed AnyTest"));  // stage 9 verify — fails, no baseline run
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "any-failure");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Equal("verify failed", outcome.Reason);
    }

    private static void InitGitRepo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "status.cs"), "old\n");
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", "chore: seed repo");
    }
}
