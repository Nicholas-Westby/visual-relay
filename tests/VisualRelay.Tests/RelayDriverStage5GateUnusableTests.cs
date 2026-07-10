using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Red-gate gate-unusability detection tests: exit code 127 (command not found),
/// "no tests found/collected" output, and zero-tests patterns cause the red gate
/// to emit an <c>author_test_gate_unusable</c> warn event instead of passing
/// vacuously.
/// </summary>
public sealed class RelayDriverStage5GateUnusableTests
{
    [Fact]
    public async Task Stage5_RedGate_ExitCode127_EmitsUnusableEventAndSkipsGate()
    {
        // When the test command produces exit code 127 (command not found),
        // the red gate must emit author_test_gate_unusable and skip the
        // pass/fail assertion rather than treating it as a satisfied red gate.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-cmd", "# Bad command\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "seed");

        var sink = new InMemoryRelayEventSink();
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(127, "command not found: non-existent"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-cmd");

        // The task must still commit (gate skipped, not flagged).
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The unusable event must be emitted.
        Assert.Contains(sink.Events, e =>
            e is { EventName: "author_test_gate_unusable", Level: "warn" });
    }

    [Fact]
    public async Task Stage5_RedGate_NoTestsCollectedOutput_EmitsUnusableEventAndSkipsGate()
    {
        // When the test runner reports "no tests collected" (zero tests found),
        // the red gate must emit author_test_gate_unusable and skip the
        // pass/fail assertion.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("no-tests-found", "# No tests found\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "seed");

        var sink = new InMemoryRelayEventSink();
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "No tests found in the specified files."),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-tests-found");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Contains(sink.Events, e =>
            e is { EventName: "author_test_gate_unusable", Level: "warn" });
    }

    [Fact]
    public async Task Stage5_RedGate_RoundNumberTestCount_DoesNotFalsePositive()
    {
        // Regression: the zero-tests detection ("0 tests") must NOT match
        // outputs with round-number test counts like "10 tests", "20 tests
        // passed", "Ran 100 tests", "230 tests". Those are genuine red-gate
        // runs where tests actually executed and failed.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("round-tests", "# Round test count\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "seed");

        var sink = new InMemoryRelayEventSink();
        // Exit code 1 with "10 tests failed" — a genuine red run, NOT unusable.
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "10 tests failed, 3 passed"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "round-tests");

        // The task must commit after a proper red gate.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // No unusable event — the round-number count was not misclassified.
        Assert.DoesNotContain(sink.Events, e =>
            e is { EventName: "author_test_gate_unusable", Level: "warn" });
    }
}
