using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The review pair used to drop the runner's own error on the floor and flag with the
/// generic "returned an invalid result", so the contract-parser message that says WHAT
/// was wrong never reached NEEDS-REVIEW or the run log. Every other stage carries it.
/// </summary>
public sealed class ReviewPairFlagReasonTests
{
    [Theory]
    [InlineData(7, "Review")]
    [InlineData(8, "Visual-review")]
    public async Task ContractFailure_FlagReasonCarriesTheParserMessage(int stage, string stageName)
    {
        const string parserMessage = "contract JSON missing required key 'verdict'";
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-contract", "# Bad contract\n");
        var runner = new FlagStageSubagentRunner(stage, parserMessage);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-contract");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains(stageName, outcome.Reason!, StringComparison.Ordinal);
        Assert.Contains(parserMessage, outcome.Reason!, StringComparison.Ordinal);
    }
}
