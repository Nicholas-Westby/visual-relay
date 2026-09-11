using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// The diff audit behind <c>authorTests.diffAudit</c>: one cheap model call over
/// what stage 5 changed, which buys the stage the same single re-ask the gate
/// buys it, and never flags or decides a check on its own.
/// </summary>
public sealed class RelayDriverStage5AuditTests
{
    private const string OneHunk =
        """{"implementationHunks":[{"file":"tests/app.tests.cs","reason":"changes the parser, not an assertion"}]}""";

    [Fact]
    public async Task Stage5_AuditOff_NeverCallsTheModel()
    {
        var (repo, sim, runner, sink) = Fixture("off", ["src/control.rs"], ".rs");
        using var owned = repo;
        var driver = Driver(runner, sim, sink, new TestRunResult(1, "1 failed"));

        await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Empty(runner.AuditCalls);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "author_test_audit");
    }

    [Fact]
    public async Task Stage5_AuditAuto_SeparateFilesOnly_NeverCallsTheModel()
    {
        var (repo, sim, runner, sink) = Fixture("auto", ["tests/app.tests.cs"]);
        using var owned = repo;
        var driver = Driver(runner, sim, sink, new TestRunResult(1, "1 failed"));

        await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Empty(runner.AuditCalls);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "author_test_audit");
    }

    [Fact]
    public async Task Stage5_AuditAuto_InlineCapableEdit_CallsTheModelOnce()
    {
        var (repo, sim, runner, sink) = Fixture("auto", ["src/control.rs"], ".rs");
        using var owned = repo;
        var driver = Driver(runner, sim, sink, new TestRunResult(1, "1 failed"));

        await driver.RunTaskAsync(repo.Root, "audit");

        var call = Assert.Single(runner.AuditCalls);
        Assert.Equal("cheap", call.Tier);
        var audit = Assert.Single(sink.Events, e => e.EventName == "author_test_audit");
        Assert.Equal("info", audit.Level);
        Assert.Equal(5, audit.StageNumber);
        Assert.Equal("auto", audit.Data!["mode"]);
        Assert.Equal("0", audit.Data["hunks"]);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "author_test_reask");
        Assert.Contains("no implementation hunks", await Ledger(repo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage5_AuditAlways_SeparateFilesOnly_CallsTheModel()
    {
        var (repo, sim, runner, sink) = Fixture("always", ["tests/app.tests.cs"]);
        using var owned = repo;
        var driver = Driver(runner, sim, sink, new TestRunResult(1, "1 failed"));

        await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Single(runner.AuditCalls);
        Assert.Equal("always", Assert.Single(sink.Events, e => e.EventName == "author_test_audit").Data!["mode"]);
    }

    [Fact]
    public async Task Stage5_AuditReportsAHunk_ReasksExactlyOnce()
    {
        var (repo, sim, runner, sink) = Fixture("always", ["tests/app.tests.cs"], auditAnswer: OneHunk);
        using var owned = repo;
        var driver = Driver(runner, sim, sink,
            new TestRunResult(1, "1 failed"), new TestRunResult(1, "1 failed"), new TestRunResult(0, "ok"));

        var outcome = await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // One audit and one re-ask: the re-asked pass is not audited again, because
        // the single re-ask it could have asked for is already spent.
        Assert.Single(runner.AuditCalls);
        Assert.Equal(2, runner.Stage5Inputs.Count);
        var reask = Assert.Single(sink.Events, e => e.EventName == "author_test_reask");
        Assert.Equal("tests/app.tests.cs", reask.Data!["files"]);
        Assert.Equal("implementation hunks in the test diff", reask.Data["reason"]);
        // The gate went red, so the message must say what the audit found and must
        // not claim the tests passed.
        Assert.Contains(
            "An audit of your test diff reported implementation changes inside the files you "
            + "listed as tests: tests/app.tests.cs.",
            runner.Stage5Inputs[1], StringComparison.Ordinal);
        Assert.DoesNotContain(
            "passed before any implementation was stripped", runner.Stage5Inputs[1], StringComparison.Ordinal);
        var audit = sink.Events.First(e => e.EventName == "author_test_audit");
        Assert.Equal("1", audit.Data!["hunks"]);
        Assert.Equal("tests/app.tests.cs", audit.Data["files"]);
        Assert.Contains("changes the parser", audit.Data["reasons"], StringComparison.Ordinal);
        // The audit alone never decides the check; the gate's own red still stands.
        Assert.Equal("red", RelayDriverStage5GateTests.Stage5Status(repo, "audit").Check);
    }

    [Fact]
    public async Task Stage5_AuditAndGateBothWantAReask_ReasksOnlyOnce()
    {
        var (repo, sim, runner, sink) = Fixture(
            "always", ["src/control.rs"], ".rs",
            """{"implementationHunks":[{"file":"src/control.rs","reason":"changes the normalizer"}]}""");
        using var owned = repo;
        // Green both times: the gate asks for the re-ask too, and there is still one.
        var driver = Driver(runner, sim, sink);

        await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Equal(2, runner.Stage5Inputs.Count);
        var reask = Assert.Single(sink.Events, e => e.EventName == "author_test_reask");
        Assert.Equal("green before implementation", reask.Data!["reason"]);
        var status = RelayDriverStage5GateTests.Stage5Status(repo, "audit");
        Assert.Equal("unproven", status.Check);
        Assert.Equal("green before implementation", status.Reason);
    }

    [Fact]
    public async Task Stage5_AuditAndReaskCosts_ReachTheStagesOwnEntry()
    {
        var (repo, sim, runner, sink) = Fixture(
            "always", ["tests/app.tests.cs"], auditAnswer: OneHunk, costReports: true);
        using var owned = repo;
        var driver = Driver(runner, sim, sink,
            new TestRunResult(1, "1 failed"), new TestRunResult(1, "1 failed"), new TestRunResult(0, "ok"));

        await driver.RunTaskAsync(repo.Root, "audit");

        // Three priced calls: the stage, its re-ask, and the one audit. The loop's
        // own sweep runs before the last two, so the entry has to be re-priced.
        var taskDirectory = Path.Combine(repo.Root, ".relay", "audit");
        var attempt1 = ReportCost(taskDirectory, "stage5-attempt1");
        var expected = attempt1
            + ReportCost(taskDirectory, "stage5-attempt2")
            + ReportCost(taskDirectory, "stage5-audit1");
        var costUsd = RelayDriverStage5GateTests.Stage5Status(repo, "audit").CostUsd;

        Assert.True(attempt1 > 0, "the scripted reports should price");
        Assert.NotNull(costUsd);
        Assert.Equal(expected, costUsd!.Value, 10);
    }

    [Fact]
    public async Task Stage5_ReaskAnswerUnusable_WarnsAndDiscardsWhatItWrote()
    {
        var (repo, sim, runner, sink) = Fixture(
            "always", ["tests/app.tests.cs"], auditAnswer: OneHunk, reaskFails: true,
            // The re-asked stage edits production code and then answers unusably.
            reaskWrites: new Dictionary<string, string> { ["src/control.rs"] = "sneaked in\n" });
        using var owned = repo;
        var driver = Driver(runner, sim, sink, new TestRunResult(1, "1 failed"));

        await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Equal(2, runner.Stage5Inputs.Count);
        var invalid = Assert.Single(
            sink.Events, e => e.EventName == "author_test_reask" && e.Data!.ContainsKey("result"));
        Assert.Equal("invalid", invalid.Data!["result"]);
        Assert.Equal("warn", invalid.Level);
        Assert.Contains("the answer was unusable", await Ledger(repo), StringComparison.Ordinal);
        // Attempt 2's edit is gone: an unreadable answer leaves no code behind.
        Assert.Equal("old\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "control.rs")));
        // The first pass's result stands, so there is one gate record, not two.
        Assert.Single(RelayDriverStage5GateTests.Stage5VerifyResults(sink));
        Assert.Equal("red", RelayDriverStage5GateTests.Stage5Status(repo, "audit").Check);
    }

    [Fact]
    public async Task Stage5_AuditCallFails_LogsTheErrorAndCarriesOn()
    {
        var (repo, sim, runner, sink) = Fixture("always", ["tests/app.tests.cs"]);
        runner.AuditFails = true;
        using var owned = repo;
        var driver = Driver(runner, sim, sink, new TestRunResult(1, "1 failed"));

        var outcome = await driver.RunTaskAsync(repo.Root, "audit");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var audit = Assert.Single(sink.Events, e => e.EventName == "author_test_audit");
        Assert.Contains("error", audit.Data!.Keys);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "author_test_reask");
        Assert.Equal("red", RelayDriverStage5GateTests.Stage5Status(repo, "audit").Check);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static (TestRepository Repo, GitSimEngine Sim, AuthorTestStageRunner Runner, InMemoryRelayEventSink Sink)
        Fixture(
            string mode,
            IReadOnlyList<string> testFiles,
            string? inlineExtension = null,
            string? auditAnswer = null,
            bool costReports = false,
            bool reaskFails = false,
            IReadOnlyDictionary<string, string>? reaskWrites = null)
    {
        var repo = TestRepository.Create();
        repo.WriteConfig("test-suite", [], testFileCmd: "test-one {files}");
        WriteDiffAudit(repo, mode, inlineExtension);
        repo.WriteTask("audit", "# Audited tests\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/control.rs", "old\n");
        sim.Commit(repo.Root, "seed");
        var runner = new AuthorTestStageRunner(
            ["src/control.rs", .. testFiles.Where(f => f != "src/control.rs")],
            testFiles,
            testFiles.ToDictionary(file => file, _ => "authored\n"))
        {
            AuditAnswer = auditAnswer,
            CostReports = costReports,
            ReaskFails = reaskFails,
            ReaskWrites = reaskWrites
        };
        return (repo, sim, runner, new InMemoryRelayEventSink());
    }

    private static RelayDriver Driver(
        AuthorTestStageRunner runner, GitSimEngine sim, InMemoryRelayEventSink sink,
        params TestRunResult[] results) =>
        new(RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(results), sink, sim),
            RelayDriverOptions.NoGitCommit);

    private static Task<string> Ledger(TestRepository repo) =>
        RelayDriverStage5GateTests.LedgerAsync(repo, "audit");

    /// <summary>What one scripted stage report prices at.</summary>
    private static double ReportCost(string taskDirectory, string stem) =>
        RelayCostEstimator.EstimateReport(Path.Combine(taskDirectory, stem + ".report.json")).CostUsd;

    /// <summary>Sets <c>authorTests.diffAudit</c>, which bootstrap only ever seeds.</summary>
    private static void WriteDiffAudit(TestRepository repo, string mode, string? inlineExtension)
    {
        RelayConfigWriter.UpsertAuthorTests(repo.Root, new TestLayoutDetection(
            [], inlineExtension is null ? [] : [inlineExtension], new Dictionary<string, int>(), 0));
        var path = Path.Combine(repo.Root, ".relay", "config.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["authorTests"]!["diffAudit"] = mode;
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
