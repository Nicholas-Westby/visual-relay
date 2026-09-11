using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The author-test gate's outcomes: it always runs when test files were declared
/// and the command is usable, and every run ends in <c>red</c>, <c>unproven</c>
/// with a reason, or a flag — each visible as a stage-5 <c>verify_result</c>.
/// </summary>
public sealed class RelayDriverStage5GateTests
{
    [Fact]
    public async Task Stage5_SuspectTestFile_IsKeptAndWarned()
    {
        // src/helper.go reads as neither a test path nor an inline-capable
        // extension: the gate keeps it (it is on the model's list) but says so.
        using var repo = TestRepository.Create();
        repo.WriteConfig("go test ./...", [], testFileCmd: "go test {files}");
        repo.WriteTask("suspect", "# Suspect scope\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.go", "old\n");
        sim.Seed(repo.Root, "src/helper.go", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/app.go", "src/helper.go"], ["src/helper.go"],
            new Dictionary<string, string> { ["src/helper.go"] = "authored\n" });
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red")), sink, sim),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "suspect");

        var warn = Assert.Single(sink.Events, e => e.EventName == "author_test_scope_suspect");
        Assert.Equal("warn", warn.Level);
        Assert.Equal("src/helper.go", warn.Data!["files"]);
        Assert.Equal("src/helper.go=suspect", warn.Data["scope"]);
        // Kept, not reverted: the file still carries what stage 5 wrote.
        Assert.Equal("authored\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "helper.go")));
        Assert.Contains("suspect entries kept: src/helper.go", await LedgerAsync(repo, "suspect"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage5_InlineCapableFile_RedRun_RecordsRedWithTheTargetedCommand()
    {
        // A Rust repo whose unit tests live in the implementation file: the only
        // declared test file is src/control.rs, and the targeted command fails.
        using var repo = TestRepository.Create();
        repo.WriteConfig("cargo test", [], testFileCmd: "cargo test {files}");
        WriteInlineExtensions(repo, ".rs");
        repo.WriteTask("inline-red", "# Inline tests\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/control.rs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/control.rs"], ["src/control.rs"],
            new Dictionary<string, string> { ["src/control.rs"] = "old\n#[test] fn t() {}\n" });
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "1 failed"), new TestRunResult(0, "ok")), sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "inline-red");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var verify = Assert.Single(Stage5VerifyResults(sink));
        Assert.Equal("cargo test src/control.rs", verify.Data!["command"]);
        Assert.Equal("red", verify.Data["check"]);
        Assert.Equal("1", verify.Data["exitCode"]);
        Assert.Equal("src/control.rs=inline-capable", verify.Data["scope"]);
        // The inline-capable file is on the list, so it is never stripped.
        Assert.Equal(string.Empty, verify.Data["strippedFiles"]);
        Assert.Equal("old\n#[test] fn t() {}\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "control.rs")));
        Assert.Equal("red", Stage5Status(repo, "inline-red").Check);
    }

    /// <summary>
    /// The one result the harness refuses outright: the command passed with the
    /// implementation stashed away, so the tests never needed the change. Pinned
    /// on the decision itself — end to end the worktree filter reverts or deletes
    /// every strip candidate before the gate ever sees one.
    /// </summary>
    [Fact]
    public void AuthorTestGate_GreenAfterStripping_IsAHardFailure()
    {
        var outcome = AuthorTestGateOutcome.ForRun(
            "dotnet test tests/app.tests.cs",
            new TestRunResult(0, "Passed!"),
            gateUnusable: false,
            ["src/app.cs"],
            [new AuthorTestScopeVerdict("tests/app.tests.cs", AuthorTestScopeKind.Separate)],
            reaskUsed: false);

        Assert.Null(outcome.Check);
        Assert.Null(outcome.CheckName);
        Assert.Equal("author-tests passed after implementation files were stripped", outcome.Reason);
        Assert.False(outcome.ReaskRequested);
    }

    /// <summary>A re-ask is asked for once; the run that follows one never asks again.</summary>
    [Fact]
    public void AuthorTestGate_GreenAfterAReask_RecordsUnprovenWithoutAnother()
    {
        AuthorTestScopeVerdict[] verdicts = [new("src/control.rs", AuthorTestScopeKind.InlineCapable)];

        var first = AuthorTestGateOutcome.ForRun(
            "cargo test src/control.rs", new TestRunResult(0, "ok"), false, [], verdicts, reaskUsed: false);
        var second = AuthorTestGateOutcome.ForRun(
            "cargo test src/control.rs", new TestRunResult(0, "ok"), false, [], verdicts, reaskUsed: true);

        Assert.True(first.ReaskRequested);
        Assert.False(second.ReaskRequested);
        Assert.All([first, second], o => Assert.Equal("green before implementation", o.Reason));
    }

    [Fact]
    public async Task Stage5_UnusableCommand_RecordsUnprovenAndWarns()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], testFileCmd: "dotnet test {files}");
        repo.WriteTask("unusable", "# Unusable gate\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/app.cs", "tests/app.tests.cs"], ["tests/app.tests.cs"],
            new Dictionary<string, string> { ["tests/app.tests.cs"] = "authored\n" });
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(127, "command not found"), new TestRunResult(0, "ok")), sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "unusable");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        AssertUnproven(repo, sink, "unusable", "gate command unusable");
        Assert.Contains(sink.Events, e => e is { EventName: "author_test_gate_unusable", Level: "warn" });
        Assert.Equal("127", Assert.Single(Stage5VerifyResults(sink)).Data!["exitCode"]);
    }

    [Fact]
    public async Task Stage5_PlaceholderCommand_RecordsUnprovenAndRunsNothing()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig(ProjectBootstrapper.PlaceholderTestCommand, []);
        repo.WriteTask("placeholder", "# Placeholder gate\n");
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

        var outcome = await driver.RunTaskAsync(repo.Root, "placeholder");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        AssertUnproven(repo, sink, "placeholder", "placeholder test command");
        Assert.Contains(sink.Events, e => e is { EventName: "author_test_gate_unusable", Level: "warn" });
        // Nothing ran, so the event reports no exit code.
        Assert.DoesNotContain("exitCode", Assert.Single(Stage5VerifyResults(sink)).Data!.Keys);
    }

    [Fact]
    public async Task Stage5_GreenWithOnlySeparateFiles_RecordsUnproven()
    {
        // Nothing to strip and the authored test passes: the gate proved nothing,
        // which is recorded rather than accepted as green coverage.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], testFileCmd: "dotnet test {files}");
        repo.WriteTask("no-strip", "# Regression coverage\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "already correct\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/app.cs", "tests/app.tests.cs"], ["tests/app.tests.cs"],
            new Dictionary<string, string> { ["tests/app.tests.cs"] = "authored\n" });
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-strip");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        AssertUnproven(repo, sink, "no-strip", "no implementation to strip");
        var verify = Assert.Single(Stage5VerifyResults(sink));
        Assert.Equal("dotnet test tests/app.tests.cs", verify.Data!["command"]);
        Assert.Equal("0", verify.Data["exitCode"]);
        Assert.Equal("tests/app.tests.cs=separate", verify.Data["scope"]);
    }

    [Fact]
    public async Task Stage5_NoTestFilesDeclared_RecordsUnproven()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], testFileCmd: "dotnet test {files}");
        repo.WriteTask("no-tests", "# No authored tests\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(["src/app.cs"], []);
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(), sink, sim),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-tests");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        AssertUnproven(repo, sink, "no-tests", "no test files declared");
        Assert.Equal(string.Empty, Assert.Single(Stage5VerifyResults(sink)).Data!["scope"]);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static void AssertUnproven(
        TestRepository repo, InMemoryRelayEventSink sink, string taskId, string reason)
    {
        var status = Stage5Status(repo, taskId);
        Assert.Equal("unproven", status.Check);
        Assert.Equal(reason, status.Reason);
        var verify = Assert.Single(Stage5VerifyResults(sink));
        Assert.Equal("unproven", verify.Data!["check"]);
        Assert.Equal(reason, verify.Data["reason"]);
        var warn = Assert.Single(sink.Events, e => e.EventName == "author_test_unproven");
        Assert.Equal("warn", warn.Level);
        Assert.Equal(reason, warn.Data!["reason"]);
    }

    internal static IReadOnlyList<RelayEvent> Stage5VerifyResults(InMemoryRelayEventSink sink) =>
        [.. sink.Events.Where(e => e is { EventName: "verify_result", StageNumber: 5 })];

    internal static StageStatusEntry Stage5Status(TestRepository repo, string taskId) =>
        StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", taskId)).Single(e => e.Stage == 5);

    internal static Task<string> LedgerAsync(TestRepository repo, string taskId) =>
        File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", taskId, "ledger.md"));

    /// <summary>Writes <c>authorTests.inlineTestExtensions</c> into the target's config.</summary>
    internal static void WriteInlineExtensions(TestRepository repo, params string[] extensions) =>
        RelayConfigWriter.UpsertAuthorTests(
            repo.Root, new TestLayoutDetection(["rust"], extensions, new Dictionary<string, int>(), 0));
}
