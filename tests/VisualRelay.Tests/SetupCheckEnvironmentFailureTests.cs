using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A red guard or bootstrap check while the tests pass is the ENVIRONMENT failing, not
/// the change. Escalating Fix-verify through it burned 75 minutes of frontier turns on a
/// sandbox that would never let the guard run, so the task is flagged on the spot.
/// </summary>
public sealed class SetupCheckEnvironmentFailureTests
{
    [Fact]
    public async Task GuardRed_WithGreenTests_FlagsAtStage10_WithoutEnteringFixVerify()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: "guard-build");
        repo.WriteTask("env-guard", "# Guard fails in this sandbox\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                        // stage 5 author gate
            new TestRunResult(1, "error: sandbox denied write"), // stage 10 guard
            new TestRunResult(0, "All 7 tests passed!"));        // stage 10 test suite
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink, new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "env-guard");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 11);
        Assert.Contains("guard-build", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("sandbox denied write", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e =>
            e is { Level: "warn", EventName: "environment_failure" });
    }

    [Fact]
    public async Task GuardGoesRedMidLoop_WithGreenTests_StopsFixVerifyImmediately()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: "guard-build", maxStageFailures: 3);
        repo.WriteTask("env-guard-loop", "# Guard breaks once the loop is running\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                     // stage 5 author gate
            new TestRunResult(0, "guard ok"),                // stage 10 guard
            new TestRunResult(1, "Failed TestX"),            // stage 10 test suite
            new TestRunResult(1, "Failed TestX"),            // stage 10 flaky retry
            new TestRunResult(1, "error: toolchain missing"), // fix-verify run 1 guard
            new TestRunResult(0, "All 7 tests passed!"));     // fix-verify run 1 tests
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink, new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "env-guard-loop");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        // One Fix-verify attempt ran before the environment broke; none after.
        Assert.Single(runner.Invocations, i => i.Stage.Number == 11);
        Assert.Contains("toolchain missing", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e =>
            e is { Level: "warn", EventName: "environment_failure" });
    }

    [Fact]
    public async Task GuardRed_WithRedTests_StillEntersFixVerify()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            guardCmd: "guard-build");
        repo.WriteTask("real-failure", "# The change itself is broken\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),            // stage 5 author gate
            new TestRunResult(1, "guard says no"),  // stage 10 guard
            new TestRunResult(1, "Failed TestX"),   // stage 10 test suite
            new TestRunResult(1, "Failed TestX"),   // stage 10 flaky retry
            new TestRunResult(0, "guard ok"),       // fix-verify run 1 guard
            new TestRunResult(0, "green"));         // fix-verify run 1 tests
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink(), new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "real-failure");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Contains(runner.Invocations, i => i.Stage.Number == 11);
    }
}
