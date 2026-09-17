using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Review (7) and Visual-review (8) run before Verify (10), so whatever Fix-verify
/// (11) edits afterwards used to go straight to Commit with nobody looking at it. On
/// i18next both tasks' Fix-verify agents edited the project's test configuration to
/// get a green suite; no reviewer ever saw that file and one of the two commits
/// carried it.
/// </summary>
public sealed class RelayDriverFixVerifyReviewTests
{
    private const string OutsidePath = "vitest.config.mts";

    /// <summary>
    /// Writes a file at stage 11 (as a real Fix-verify agent would), optionally
    /// amends the manifest from that stage, and answers the second review pass with a
    /// chosen verdict while recording every invocation it sees.
    /// </summary>
    private sealed class FixVerifyEditingRunner(
        string editedPath, string? amendManifest = null, string verdict = "pass") : ISubagentRunner
    {
        private readonly ScriptedSubagentRunner _inner = Seeded();
        private readonly List<StageInvocation> _invocations = [];

        private static ScriptedSubagentRunner Seeded()
        {
            var runner = new ScriptedSubagentRunner();
            runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
            return runner;
        }

        public IReadOnlyList<StageInvocation> SecondReviews =>
            [.. _invocations.Where(i => i.Stage.Name == "Fix-verify-review")];

        public Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            _invocations.Add(inv);

            if (inv.Stage.Name == "Fix-verify-review")
            {
                var issues = verdict == "pass"
                    ? "[]"
                    : """["the project's test configuration was changed to pass the suite"]""";
                return Json($$"""{"verdict":"{{verdict}}","issues":{{issues}}}""");
            }

            if (inv.Stage.Number == 11)
            {
                var full = Path.Combine(inv.TargetRoot, editedPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "process.env.TZ = 'UTC'\n");
                return amendManifest is null
                    ? Json("""{"summary":"fixed verify"}""")
                    : Json($$"""{"summary":"fixed verify","amendManifest":["{{amendManifest}}"]}""");
            }

            return _inner.RunAsync(inv, ct);
        }

        private static Task<SubagentResult> Json(string json) =>
            Task.FromResult(new SubagentResult(
                $"```json{Environment.NewLine}{json}{Environment.NewLine}```", json, true, null));
    }

    private static (RelayDriver Driver, InMemoryRelayEventSink Sink) Build(
        TestRepository repo, string taskId, ISubagentRunner runner)
    {
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask(taskId, $"# {taskId}\n");
        // A real (simulated) repository, because the unreviewed-edit check reads the
        // working tree's changed paths through git; the git-free helper answers
        // "not a git repository" to every probe.
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);
        sim.Seed(repo.Root, "src/app.cs", "// app\n");
        sim.Seed(repo.Root, "tests/app.tests.cs", "// tests\n");
        sim.Commit(repo.Root, "seed the plan's files");
        var sink = new InMemoryRelayEventSink();
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),           // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),  // stage 10 verify — first run fails
            new TestRunResult(1, "Failed TestX"),  // stage 10 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),  // fix-verify attempt 1 — red
            new TestRunResult(0, "green"));        // fix-verify attempt 1 retry — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink, sim),
            RelayDriverOptions.NoGitCommit);
        return (driver, sink);
    }

    private static IEnumerable<RelayEvent> UnreviewedEvents(InMemoryRelayEventSink sink) =>
        sink.Events.Where(e => e.EventName == "fix_verify_unreviewed_edits");

    [Fact]
    public async Task AGreenFixVerifyThatOnlyTouchedThePlan_CommitsWithoutASecondReview()
    {
        using var repo = TestRepository.Create();
        var runner = new FixVerifyEditingRunner("src/app.cs");
        var (driver, sink) = Build(repo, "inside-the-plan", runner);

        var outcome = await driver.RunTaskAsync(repo.Root, "inside-the-plan");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Empty(runner.SecondReviews);
        Assert.Empty(UnreviewedEvents(sink));
    }

    [Fact]
    public async Task AGreenFixVerifyThatEditedOutsideThePlan_IsReviewedOnThosePathsOnly()
    {
        using var repo = TestRepository.Create();
        var runner = new FixVerifyEditingRunner(OutsidePath);
        var (driver, sink) = Build(repo, "outside-the-plan", runner);

        var outcome = await driver.RunTaskAsync(repo.Root, "outside-the-plan");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var review = Assert.Single(runner.SecondReviews);
        Assert.Equal("cheap", review.Tier);
        Assert.Contains(OutsidePath, review.TaskInput, StringComparison.Ordinal);
        Assert.DoesNotContain("src/app.cs", review.TaskInput, StringComparison.Ordinal);

        var published = Assert.Single(UnreviewedEvents(sink));
        Assert.Equal(OutsidePath, published.Data!["paths"]);
        Assert.Equal(11, published.StageNumber);
    }

    [Fact]
    public async Task AChangesVerdictOnThoseEdits_FlagsWithThePathInTheReason()
    {
        using var repo = TestRepository.Create();
        var runner = new FixVerifyEditingRunner(OutsidePath, verdict: "changes");
        var (driver, sink) = Build(repo, "reviewer-objects", runner);

        var outcome = await driver.RunTaskAsync(repo.Root, "reviewer-objects");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("fix-verify edited", outcome.Reason!, StringComparison.Ordinal);
        Assert.Contains(OutsidePath, outcome.Reason!, StringComparison.Ordinal);
        Assert.Contains("outside the plan", outcome.Reason!, StringComparison.Ordinal);
        Assert.Single(UnreviewedEvents(sink));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "reviewer-objects", "NEEDS-REVIEW")));
    }

    [Fact]
    public async Task AnAmendManifestNamingTheFile_CountsAsInThePlan()
    {
        using var repo = TestRepository.Create();
        var runner = new FixVerifyEditingRunner(OutsidePath, amendManifest: OutsidePath);
        var (driver, sink) = Build(repo, "amended-the-plan", runner);

        var outcome = await driver.RunTaskAsync(repo.Root, "amended-the-plan");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Empty(runner.SecondReviews);
        Assert.Empty(UnreviewedEvents(sink));
    }
}
