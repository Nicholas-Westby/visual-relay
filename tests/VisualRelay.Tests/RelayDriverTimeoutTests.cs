using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverTimeoutTests
{
    /// <summary>
    /// When the stage-9 baseline verify times out, the driver must fail fast
    /// with a clear, actionable message — not a generic "verify failed".
    /// The message must carry the TestTimeoutHint instructing the agent to
    /// fall back to a targeted subset via TestFileCommand {files}.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_Stage9TestCommandTimesOut_FlagsWithSubsetGuidance()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("hung-suite", "# Hung suite\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"), // stage 5 red gate — passes
            new TestRunResult(-1, TimeoutSimulatingTestRunner.Output, TimedOut: true)); // stage 9 — timeout
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "hung-suite");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        // The reason must carry the actionable subset-guidance from
        // ErrorHintClassifier.TestTimeoutHint, not a generic "verify failed".
        Assert.Contains("targeted subset", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{files}", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("300000ms", outcome.Reason, StringComparison.Ordinal);
    }
}
