using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RedGateApplicabilityTests
{
    /// <summary>
    /// A code-only change (e.g. a .axaml markup file) with no authored tests must
    /// still trigger the red gate — XAML is implementation code and the gate must
    /// be language-agnostic. The driver must flag the task because the authored
    /// tests never went red (there are none).
    /// </summary>
    [Fact]
    public async Task AxamlOnlyChange_TriggersRedGate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("tweak-markup", "# Tweak markup\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedCodeOnly("src/Panel.axaml");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "tweak-markup");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("author-tests did not go red", outcome.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A change consisting entirely of non-code files (documentation, config)
    /// has no implementation to test. The red gate must be skipped and the
    /// task committed without requiring a failing test.
    /// </summary>
    [Fact]
    public async Task NonCodeOnlyChange_SkipsRedGateAndCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("update-readme", "# Update README\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedNonCodeOnly("docs/README.md");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "update-readme");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    /// <summary>
    /// A test-only change (adding a regression test for already-correct behavior)
    /// must be allowed to commit without flagging. The red gate must be skipped
    /// when every manifest entry is a declared test file — there is no
    /// implementation code to strip.
    /// </summary>
    [Fact]
    public async Task TestOnlyChange_SkipsRedGateAndCommits()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("add-regression-test", "# Add regression test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedTestOnly("tests/regression.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                runner,
                new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-regression-test");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }
}
