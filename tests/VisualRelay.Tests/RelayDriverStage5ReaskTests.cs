using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The single re-ask: tests that pass before anything was stripped, on a file
/// that can carry implementation, buy the model exactly one more attempt at
/// stage 5 — and the second result is judged the same way as the first.
/// </summary>
public sealed class RelayDriverStage5ReaskTests
{
    /// <summary>The message attempt 2 must carry, with the file list filled in.</summary>
    private const string ReaskMessage =
        "Your new tests passed before any implementation was stripped, and at least one file "
        + "you listed as a test file can carry implementation: src/control.rs. Either the change "
        + "was implemented inside a test file, or the tests do not exercise the change. Remove "
        + "every implementation change from the test files so the new tests fail against the "
        + "current code, or rewrite the tests so they fail. Do not touch implementation files.";

    [Fact]
    public async Task Stage5_GreenBeforeImplementation_ReasksOnceThenRecordsUnproven()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("cargo test", [], testFileCmd: "cargo test {files}");
        RelayDriverStage5GateTests.WriteInlineExtensions(repo, ".rs");
        repo.WriteTask("reask", "# Inline tests\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/control.rs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/control.rs"], ["src/control.rs"],
            new Dictionary<string, string> { ["src/control.rs"] = "new\n#[test] fn t() {}\n" });
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "reask");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Exactly one re-ask: stage 5 ran twice, and only the second input carries it.
        Assert.Equal(2, runner.Stage5Inputs.Count);
        Assert.DoesNotContain(ReaskMessage, runner.Stage5Inputs[0], StringComparison.Ordinal);
        Assert.Contains(ReaskMessage, runner.Stage5Inputs[1], StringComparison.Ordinal);

        var reask = Assert.Single(sink.Events, e => e.EventName == "author_test_reask");
        Assert.Equal("info", reask.Level);
        Assert.Equal("green before implementation", reask.Data!["reason"]);
        Assert.Equal("src/control.rs", reask.Data["files"]);

        var unproven = Assert.Single(sink.Events, e => e.EventName == "author_test_unproven");
        Assert.Equal("green before implementation", unproven.Data!["reason"]);

        var status = RelayDriverStage5GateTests.Stage5Status(repo, "reask");
        Assert.Equal("unproven", status.Check);
        Assert.Equal("green before implementation", status.Reason);

        // One verify_result per gate run, each naming the attempt it belongs to.
        var verifies = RelayDriverStage5GateTests.Stage5VerifyResults(sink);
        Assert.Equal(2, verifies.Count);
        Assert.Equal([1, 2], verifies.Select(v => v.Attempt));
        Assert.All(verifies, v => Assert.Equal("unproven", v.Data!["check"]));

        var ledger = await RelayDriverStage5GateTests.LedgerAsync(repo, "reask");
        Assert.Contains("Re-ask (stage 5)", ledger, StringComparison.Ordinal);
        Assert.Contains("green before implementation", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage5_ReaskGoesRed_RecordsRedWithoutASecondReask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("cargo test", [], testFileCmd: "cargo test {files}");
        RelayDriverStage5GateTests.WriteInlineExtensions(repo, ".rs");
        repo.WriteTask("reask-red", "# Inline tests\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/control.rs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/control.rs"], ["src/control.rs"],
            new Dictionary<string, string> { ["src/control.rs"] = "new\n#[test] fn t() {}\n" });
        var sink = new InMemoryRelayEventSink();
        // Gate 1 green (triggers the re-ask), gate 2 red, stage 10 green.
        var tests = new ScriptedTestRunner(
            new TestRunResult(0, "ok"), new TestRunResult(1, "1 failed"), new TestRunResult(0, "ok"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink, sim), RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "reask-red");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(2, runner.Stage5Inputs.Count);
        Assert.Single(sink.Events, e => e.EventName == "author_test_reask");
        var status = RelayDriverStage5GateTests.Stage5Status(repo, "reask-red");
        Assert.Equal("red", status.Check);
        Assert.Null(status.Reason);
        // The stage as a whole is proven red, so no unproven warning survives it.
        Assert.DoesNotContain(sink.Events, e => e.EventName == "author_test_unproven");
        var verifies = RelayDriverStage5GateTests.Stage5VerifyResults(sink);
        Assert.Equal(2, verifies.Count);
        Assert.Equal("red", verifies[1].Data!["check"]);
    }

    [Fact]
    public async Task Stage5_GreenWithOnlySeparateFiles_NeverReasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], testFileCmd: "dotnet test {files}");
        repo.WriteTask("no-reask", "# Separate tests\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/app.cs", "tests/app.tests.cs"], ["tests/app.tests.cs"],
            new Dictionary<string, string> { ["tests/app.tests.cs"] = "authored\n" });
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), sink, sim),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "no-reask");

        Assert.Single(runner.Stage5Inputs);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "author_test_reask");
        Assert.Single(RelayDriverStage5GateTests.Stage5VerifyResults(sink));
    }
}
