using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverBaselineVerifyTests
{
    private const string MochaNotFound = "sh: line 1: mocha: command not found\n";
    private const string JestNotFound = "sh: line 1: jest: command not found\n";
    [Fact]
    public async Task BaselineVerify_True_PreExistingFailure_DoesNotFlag()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true);
        repo.WriteTask("pre-existing-fail", "# Pre-existing failure\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");

        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "Failed OldTest"),   // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed OldTest"),   // stage 9 verify working — first run fails
            new TestRunResult(1, "Failed OldTest"),   // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed OldTest"));  // stage 9 verify baseline — same failure
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "pre-existing-fail");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task BaselineVerify_True_NewFailure_FlagsWithNewFailures()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true, enableFixVerify: false);
        repo.WriteTask("new-failure", "# New failure\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");

        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed OldTest\nFailed NewTest"),         // stage 9 working — OldTest + NewTest
            new TestRunResult(1, "Failed OldTest"));                        // stage 9 baseline — only OldTest
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, new InMemoryRelayEventSink(), sim),
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
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: false);
        repo.WriteTask("any-failure", "# Any failure\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed AnyTest"),    // stage 9 verify — first run fails
            new TestRunResult(1, "Failed AnyTest"));   // stage 9 verify — retry also fails
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "any-failure");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Equal("verify failed", outcome.Reason);
    }

    [Fact]
    public async Task VerifyFixLoop_StableFailureSignature_EnrichesReasonWithSignatureAndAdvisory()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("npm test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("stable-sig", "# Stable sig\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5
            new TestRunResult(1, MochaNotFound),                            // stage 10 first
            new TestRunResult(1, MochaNotFound),                            // stage 10 retry
            new TestRunResult(1, MochaNotFound),                            // fv-1 gate
            new TestRunResult(1, MochaNotFound),                            // fv-1 retry
            new TestRunResult(1, MochaNotFound),                            // fv-2 gate
            new TestRunResult(1, MochaNotFound),                            // fv-2 retry
            new TestRunResult(1, MochaNotFound),                            // fv-3 gate
            new TestRunResult(1, MochaNotFound));                           // fv-3 retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "stable-sig");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("verify failed after 3 fix-verify attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains(" — last: ", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("mocha: command not found", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("stage11-attempt3.verify-output.txt", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("identical failure across all attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("likely environment/harness", outcome.Reason, StringComparison.Ordinal);

        var warn = sink.Events.FirstOrDefault(e => e.Level == "warn" && e.EventName == "verify_identical_failures");
        Assert.NotNull(warn);
    }

    [Fact]
    public async Task VerifyFixLoop_DifferingFailureSignatures_CarriesLastSignatureOnly()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("npm test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("diff-sig", "# Diff sig\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5
            new TestRunResult(1, "Failed FooTest"),                         // stage 10 first
            new TestRunResult(1, "Failed FooTest"),                         // stage 10 retry
            new TestRunResult(1, "Failed BarTest"),                         // fv-1 gate
            new TestRunResult(1, "Failed BarTest"),                         // fv-1 retry
            new TestRunResult(1, "Failed BazTest"),                         // fv-2 gate
            new TestRunResult(1, "Failed BazTest"),                         // fv-2 retry
            new TestRunResult(1, MochaNotFound),                            // fv-3 gate
            new TestRunResult(1, MochaNotFound));                           // fv-3 retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "diff-sig");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("verify failed after 3 fix-verify attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains(" — last: ", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("mocha: command not found", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("identical failure across all attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "verify_identical_failures");
    }

    [Fact]
    public async Task VerifyFixLoop_EnrichedReason_FlowsToNeedsReviewFileAndReviewReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("npm test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("needs-review-flow", "# Needs review flow\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5
            new TestRunResult(1, MochaNotFound),                            // stage 10 first
            new TestRunResult(1, MochaNotFound),                            // stage 10 retry
            new TestRunResult(1, MochaNotFound),                            // fv-1 gate
            new TestRunResult(1, MochaNotFound),                            // fv-1 retry
            new TestRunResult(1, MochaNotFound),                            // fv-2 gate
            new TestRunResult(1, MochaNotFound),                            // fv-2 retry
            new TestRunResult(1, MochaNotFound),                            // fv-3 gate
            new TestRunResult(1, MochaNotFound));                           // fv-3 retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "needs-review-flow");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);

        // NEEDS-REVIEW file first line contains the enriched reason (propagates to
        // FailedRunContextReader.FlagReason → FixTaskAuthorRunner.BuildPrompt).
        var review = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "needs-review-flow", "NEEDS-REVIEW"));
        Assert.Contains("mocha: command not found", review, StringComparison.Ordinal);
        Assert.Contains("stage11-attempt3.verify-output.txt", review, StringComparison.Ordinal);

        // RelayTaskItem built from the reason flows it to /state reviewReason + UI.
        var item = new RelayTaskItem(
            "needs-review-flow", "/tmp/t.md", "/tmp", false, [],
            ReviewReason: outcome.Reason);
        Assert.True(item.NeedsReview);
        Assert.Contains("mocha: command not found", item.ReviewReason!, StringComparison.Ordinal);
    }
}
