using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Pair-orchestration tests for the parallel Visual-review stage.
/// </summary>
public sealed partial class RelayDriverReviewPairTests
{
    [Fact]
    public async Task RunTaskAsync_VisualReviewStage_LedgerRecordsStage()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("vision-input", "# Vision input\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/Views/MainWindow.axaml", "tests/ui.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "vision-input");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var taskDir = Path.Combine(repo.Root, ".relay", "vision-input");
        var ledger = await File.ReadAllTextAsync(Path.Combine(taskDir, "ledger.md"));
        Assert.Contains("## Stage 8 - Visual-review", ledger, StringComparison.Ordinal);
        Assert.Contains("\"verdict\"", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_VisualRenderCmdMissing_AttachmentOnly()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("attach-only", "# Attachment only\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "attach-only");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "attach-only", "ledger.md"));
        Assert.Contains("## Stage 8 - Visual-review", ledger, StringComparison.Ordinal);
        Assert.Contains("\"verdict\"", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_HappyPath_ReviewAndVisualReviewRecordedInFixedOrder()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("happy-path", "# Happy path\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "happy-path");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "happy-path", "ledger.md"));
        var reviewPos = ledger.IndexOf("## Stage 7 - Review", StringComparison.Ordinal);
        var visualPos = ledger.IndexOf("## Stage 8 - Visual-review", StringComparison.Ordinal);
        Assert.True(reviewPos >= 0, "Ledger should contain Review section");
        Assert.True(visualPos >= 0, "Ledger should contain Visual-review section");
        Assert.True(reviewPos < visualPos, "Review should appear before Visual-review in ledger");
    }

    [Fact]
    public async Task RunTaskAsync_TriageSkip_NoVisualReviewInvocation()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("docs-only", "# Docs only\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new TriageSkipSubagentRunner();
        runner.SeedHappyPath("docs/README.md", "tests/docs.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "docs-only");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "docs-only", "ledger.md"));
        Assert.Contains("## Stage 8 - Visual-review", ledger, StringComparison.Ordinal);
        Assert.Contains("_Skipped:", ledger, StringComparison.Ordinal);
        Assert.Contains("no visual changes", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ReviewInvalid_SiblingFinishesThenFlags()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-review", "# Bad review\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new FlagStageSubagentRunner(7);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-review");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("invalid", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTaskAsync_VisualReviewInvalid_FlagsAfterBothComplete()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-visual", "# Bad visual\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new FlagStageSubagentRunner(8);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-visual");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("invalid", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTaskAsync_ReviewAndVisualReviewEventsPublished()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("events-test", "# Events test\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "events-test");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 7 });
        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 8 });
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 7 });
        Assert.Contains(sink.Events, e => e is { EventName: "stage_done", StageNumber: 8 });
    }

    [Fact]
    public async Task RunTaskAsync_ResumeAtStage8_RedirectsToStage7()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("resume-8", "# Resume at 8\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "resume-8");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "resume-8", "ledger.md"));
        Assert.Contains("## Stage 7 - Review", ledger, StringComparison.Ordinal);
        Assert.Contains("## Stage 8 - Visual-review", ledger, StringComparison.Ordinal);
    }
}

/// <summary>
/// Returns <c>{"visualReview":"skip","reason":"no visual changes"}</c> for
/// the triage stage (0) so the visual-review is skipped.
/// </summary>
internal sealed class TriageSkipSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 0)
        {
            return Task.FromResult(new SubagentResult(
                RawText: """```json\n{"visualReview":"skip","reason":"no visual changes"}\n```""",
                Json: """{"visualReview":"skip","reason":"no visual changes"}""",
                IsValid: true,
                Error: null));
        }
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Wraps a <see cref="ScriptedSubagentRunner"/> and returns an invalid result
/// for the specified <paramref name="flagAtStage"/>, simulating a stage that
/// produces no valid JSON.
/// </summary>
internal sealed class FlagStageSubagentRunner(int flagAtStage) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == flagAtStage)
        {
            Directory.CreateDirectory(invocation.TraceDirectory);
            return new SubagentResult(RawText: string.Empty, Json: null, IsValid: false, Error: "invalid result");
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
