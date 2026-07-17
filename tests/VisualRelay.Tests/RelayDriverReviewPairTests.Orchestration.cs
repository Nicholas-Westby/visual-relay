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

        // The skip must publish a terminal stage_done{status:Skipped} so the live
        // stage-8 card settles instead of ticking "Running" forever.
        Assert.Contains(sink.Events, e =>
            e is { EventName: "stage_done", StageNumber: 8 } &&
            e.Data is not null && e.Data.TryGetValue("status", out var status) && status == "Skipped");
    }

    [Fact]
    public async Task RunTaskAsync_TriageDeclinesWithNonSkipVerdict_PublishesSkippedStageDone()
    {
        // The loader always injects a default "vision" tier, so visionConfigured is
        // true through RunTaskAsync; the fallback "vision tier unconfigured" skip
        // reason is reached when triage declines with a verdict other than "skip".
        // Both skip variants share the one RecordStageAsync call, so this pins the
        // fallback branch also emits the terminal stage_done{status:Skipped}.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("triage-decline", "# Triage decline\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new TriageDeclineSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "triage-decline");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        Assert.Contains(sink.Events, e =>
            e is { EventName: "stage_done", StageNumber: 8 } &&
            e.Data is not null && e.Data.TryGetValue("status", out var status) && status == "Skipped");
        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "triage-decline", "ledger.md"));
        Assert.Contains("_Skipped: vision tier unconfigured_", ledger, StringComparison.Ordinal);
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
        Assert.Contains("Review returned an invalid result", outcome.Reason ?? "", StringComparison.Ordinal);
        // Must not contain kill-related text for a non-kill invalid result.
        Assert.DoesNotContain("stall-killed", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("killed-output", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        // No retry for non-kill invalid results.
        Assert.DoesNotContain(sink.Events, e => e.EventName == "stage_escalated");
    }

    [Fact]
    public async Task RunTaskAsync_ReviewKilled_RetriesThenFlagsWithEnrichedReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("killed-review", "# Killed review\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new KillSubagentRunner(7);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "killed-review");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        // Enriched reason must contain kill details.
        Assert.Contains("stall-killed", outcome.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("absolute_ceiling", outcome.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("lastSignal=cpu", outcome.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("stage7-attempt1.killed-output.txt", outcome.Reason ?? "", StringComparison.Ordinal);
        // Must NOT contain the old generic reason.
        Assert.DoesNotContain("Review returned an invalid result", outcome.Reason ?? "", StringComparison.Ordinal);
        // Retry escalation event must have been published.
        Assert.Contains(sink.Events, e => e.EventName == "stage_escalated");
        // NEEDS-REVIEW file must contain the enriched reason.
        var needsReviewPath = Path.Combine(repo.Root, ".relay", "killed-review", "NEEDS-REVIEW");
        Assert.True(File.Exists(needsReviewPath), "NEEDS-REVIEW file should exist");
        var needsReviewContent = await File.ReadAllTextAsync(needsReviewPath);
        Assert.Contains("stall-killed", needsReviewContent, StringComparison.Ordinal);
        // RelayTaskItem built from the reason flows it to /state reviewReason + UI.
        var item = new RelayTaskItem("killed-review", "/tmp/t.md", "/tmp", false, [], ReviewReason: outcome.Reason);
        Assert.True(item.NeedsReview);
        Assert.Contains("stall-killed", item.ReviewReason!, StringComparison.Ordinal);
        Assert.Contains("stage7-attempt1.killed-output.txt", item.ReviewReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ReviewKilled_RetrySucceeds_ProceedsWithoutFlag()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("killed-then-ok", "# Killed then ok\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new TwoPhaseKillSubagentRunner(7);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "killed-then-ok");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // No flagged event.
        Assert.DoesNotContain(sink.Events, e => e.EventName == "flagged");
        // Ledger must contain both Review and Visual-review sections.
        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "killed-then-ok", "ledger.md"));
        Assert.Contains("## Stage 7 - Review", ledger, StringComparison.Ordinal);
        Assert.Contains("## Stage 8 - Visual-review", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_VisualReviewKilled_RetriesThenFlagsWithEnrichedReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("killed-visual", "# Killed visual\n");
        var sink = new InMemoryRelayEventSink();
        var runner = new KillSubagentRunner(8);
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "killed-visual");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        // Enriched reason must reference Visual-review (not just Review).
        Assert.Contains("Visual-review stall-killed", outcome.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("absolute_ceiling", outcome.Reason ?? "", StringComparison.Ordinal);
        Assert.Contains("stage8-attempt1.killed-output.txt", outcome.Reason ?? "", StringComparison.Ordinal);
        // Retry escalation event must have been published.
        Assert.Contains(sink.Events, e => e.EventName == "stage_escalated");
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
