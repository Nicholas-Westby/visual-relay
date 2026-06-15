using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Baseline-verify tests, split out of RelayDriverTests.cs to keep each file
// under the 300-line guard. Uses InitGitRepo from the main partial class.
public sealed partial class RelayDriverTests
{
    [Fact]
    public async Task BaselineVerify_True_PreExistingFailure_DoesNotFlag()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("full-suite", [], baselineVerify: true);
        repo.WriteTask("pre-existing-fail", "# Pre-existing failure\n");
        InitGitRepo(repo.Root);

        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "Failed OldTest"),   // stage 5 author gate — red (passes)
            new TestRunResult(1, "Failed OldTest"),   // stage 9 verify working — first run fails
            new TestRunResult(1, "Failed OldTest"),   // stage 9 verify — retry also fails
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
            new TestRunResult(1, "Failed AnyTest"),    // stage 9 verify — first run fails
            new TestRunResult(1, "Failed AnyTest"));   // stage 9 verify — retry also fails
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "any-failure");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Equal("verify failed", outcome.Reason);
    }
}
