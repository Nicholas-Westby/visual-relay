using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The driver's mechanical Fix-skip rules: Fix (9) is skipped only when the whole
/// review family (Review 7 + Visual-review 8) is a clean pass or skipped, and
/// Fix-verify (11) is skipped when Verify (10) is green. Every branch fails open —
/// any non-pass, non-empty issues, or malformed verdict still runs Fix.
/// </summary>
public sealed class RelayDriverSkipFixTests
{
    private static RelayDriver Driver(TestRepository repo, ISubagentRunner runner, IRelayEventSink sink) =>
        new(RelayDriverTestHelpers.DepsFor(repo, runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

    [Fact]
    public async Task CleanReviewFamily_GreenVerify_SkipsFixAndFixVerify_RecordedAndProceeds()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("clean", "# Clean review\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs"); // Review + Visual both pass
        var sink = new InMemoryRelayEventSink();
        var driver = Driver(repo, runner, sink);

        var outcome = await driver.RunTaskAsync(repo.Root, "clean");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Fix (9) and Fix-verify (11) never launch a subagent; Verify (10) still runs.
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 9);
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 11);
        Assert.Contains(runner.Invocations, i => i.Stage.Number == 10);

        var taskDir = Path.Combine(repo.Root, ".relay", "clean");
        var entries = StageStatusRecord.Read(taskDir);
        var fix = entries.Single(e => e.Stage == 9);
        Assert.Equal("Skipped", fix.Status);
        Assert.Equal("green", fix.Check);
        Assert.Null(fix.CostUsd);
        Assert.Equal("Skipped", entries.Single(e => e.Stage == 11).Status);

        // Ledger records both skips explicitly; seal chain covers the skipped stages.
        var ledger = await File.ReadAllTextAsync(Path.Combine(taskDir, "ledger.md"));
        Assert.Contains("## Stage 9 - Fix", ledger, StringComparison.Ordinal);
        Assert.Contains("review passed with no issues", ledger, StringComparison.Ordinal);
        Assert.Contains("## Stage 11 - Fix-verify", ledger, StringComparison.Ordinal);
        var seals = await File.ReadAllLinesAsync(Path.Combine(taskDir, "clean.seals"));
        Assert.Contains(seals, s => s.Contains("\"n\":9", StringComparison.Ordinal));

        // The skip settles the live card via a terminal stage_done{status:Skipped}.
        Assert.Contains(sink.Events, e =>
            e is { EventName: "stage_done", StageNumber: 9 } &&
            e.Data is not null && e.Data.TryGetValue("status", out var s) && s == "Skipped");
    }

    [Fact]
    public async Task ReviewPassWithIssues_RunsFix()
    {
        // A "pass" verdict carrying warnings is NOT clean — Fix must resolve them.
        await AssertFixRuns("pass-issues",
            new StageBodySubagentRunner((7, """{"verdict":"pass","issues":["a lingering warning"]}""")));
    }

    [Fact]
    public async Task ReviewChangesVerdict_RunsFix()
    {
        await AssertFixRuns("changes",
            new StageBodySubagentRunner((7, """{"verdict":"changes","issues":[]}""")));
    }

    [Fact]
    public async Task ReviewIssuesPropertyMissing_RunsFix()
    {
        // verdict==pass but no issues array at all → uncertain → fail open.
        await AssertFixRuns("no-issues-key",
            new StageBodySubagentRunner((7, """{"verdict":"pass"}""")));
    }

    [Fact]
    public async Task MalformedReviewVerdict_RunsFix()
    {
        // Truncated/unparseable verdict must never trigger the skip.
        await AssertFixRuns("malformed",
            new StageBodySubagentRunner((7, """{"verdict":"pass","issues":[""")));
    }

    [Fact]
    public async Task VisualReviewReportsFindings_RunsFix()
    {
        // Review passes clean but Visual-review flags a defect → Fix still runs.
        await AssertFixRuns("visual-findings",
            new StageBodySubagentRunner((8, """{"verdict":"changes","issues":["clipped corner"]}""")));
    }

    private async Task AssertFixRuns(string taskId, StageBodySubagentRunner runner)
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask(taskId, $"# {taskId}\n");
        var driver = Driver(repo, runner, new InMemoryRelayEventSink());

        var outcome = await driver.RunTaskAsync(repo.Root, taskId);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        Assert.Contains(runner.Invocations, i => i.Stage.Number == 9);
        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", taskId));
        Assert.Equal("Done", entries.Single(e => e.Stage == 9).Status);
    }

    [Fact]
    public async Task ResumeAfterSkippedFix_DoesNotReRunFix()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("resume-skip", "# Resume over skipped fix\n");

        // Run 1: clean review → Fix (9) skipped; the Verify (10) agent then flags.
        var flagAt10 = new FlagAtStageSubagentRunner(flagAtStage: 10);
        var driver1 = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, flagAt10,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome1 = await driver1.RunTaskAsync(repo.Root, "resume-skip");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);

        var taskDir = Path.Combine(repo.Root, ".relay", "resume-skip");
        var status1 = StageStatusRecord.Read(taskDir);
        Assert.Equal("Skipped", status1.Single(e => e.Stage == 9).Status);
        Assert.Equal("Flagged", status1.Single(e => e.Stage == 10).Status);
        Assert.False(File.Exists(Path.Combine(taskDir, "stage9-attempt1.report.json")));

        // Run 2: resume re-enters at the flagged Verify (10), never re-running Fix (9).
        var runner2 = new ScriptedSubagentRunner();
        runner2.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver2 = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner2,
                new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: false, Resume: true));
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "resume-skip");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        Assert.False(File.Exists(Path.Combine(taskDir, "stage9-attempt1.report.json")));
        Assert.False(File.Exists(Path.Combine(taskDir, "stage9-attempt2.report.json")));
        Assert.Equal("Skipped", StageStatusRecord.Read(taskDir).Single(e => e.Stage == 9).Status);
    }
}

/// <summary>
/// Drives the scripted happy path but substitutes a fixed body for the given
/// stage numbers, so a test can pin the exact Review (7) / Visual-review (8)
/// verdict the Fix-skip decision reads. Records every invocation for assertions.
/// </summary>
internal sealed class StageBodySubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private readonly IReadOnlyDictionary<int, string> _bodies;
    private readonly List<StageInvocation> _invocations = [];

    public IReadOnlyList<StageInvocation> Invocations => _invocations;

    public StageBodySubagentRunner(params (int Stage, string Body)[] bodies)
    {
        _bodies = bodies.ToDictionary(b => b.Stage, b => b.Body);
        _inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        _invocations.Add(invocation);
        return _bodies.TryGetValue(invocation.Stage.Number, out var body)
            ? Task.FromResult(new SubagentResult(body, body, true, null))
            : _inner.RunAsync(invocation, cancellationToken);
    }
}
