using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverEmptyTaskInputTests
{
    [Fact]
    public async Task Run_TaskFolderMissing_FailsWithNeedsReview()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Ensure the tasks directory exists on disk so the gate fires
        // (this mirrors the incident: the tasks dir existed but the
        // specific task folder was deleted mid-drain).
        Directory.CreateDirectory(Path.Combine(repo.Root, "llm-tasks"));

        // No task folder exists — task "ghost" was never written.
        var sink = new InMemoryRelayEventSink();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "ghost");

        // Outcome is Flagged, not Committed.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);

        // NEEDS-REVIEW marker exists with the expected reason.
        var needsReviewPath = Path.Combine(repo.Root, ".relay", "ghost", "NEEDS-REVIEW");
        Assert.True(File.Exists(needsReviewPath), "NEEDS-REVIEW marker should exist");
        var firstLine = (await File.ReadAllTextAsync(needsReviewPath)).Split('\n')[0].Trim();
        Assert.Contains("task spec missing or empty", firstLine, StringComparison.Ordinal);

        // No stage 1 input was written (gate fires before any stage runs).
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "ghost", "stage1-attempt1.input.json")));

        // An error event was published.
        Assert.Contains(sink.Events, e => e.EventName == "empty_task_input" && e.Level == "error");
    }

    [Fact]
    public async Task Run_TaskMarkdownWhitespaceOnly_FailsWithNeedsReview()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Folder exists but markdown is whitespace-only.
        repo.WriteNestedTask("whitespace", "\n\n");

        var sink = new InMemoryRelayEventSink();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "whitespace");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);

        var needsReviewPath = Path.Combine(repo.Root, ".relay", "whitespace", "NEEDS-REVIEW");
        Assert.True(File.Exists(needsReviewPath));
        var firstLine = (await File.ReadAllTextAsync(needsReviewPath)).Split('\n')[0].Trim();
        Assert.Contains("task spec missing or empty", firstLine, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "whitespace", "stage1-attempt1.input.json")));

        Assert.Contains(sink.Events, e => e.EventName == "empty_task_input" && e.Level == "error");
    }

    [Fact]
    public async Task Run_NormalTask_GateIsNoOp()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("healthy", "# Healthy task\n");

        var sink = new InMemoryRelayEventSink();
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "healthy");

        // The task should run through — gate was a no-op.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // No empty_task_input event was fired.
        Assert.DoesNotContain(sink.Events, e => e.EventName == "empty_task_input");
    }
}
