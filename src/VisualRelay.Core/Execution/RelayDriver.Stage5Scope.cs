using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// The scope check over stage 5's declared test files, and the events and ledger
// lines that make every author-test outcome readable after the run.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Classifies every file stage 5 called a test. Entries the classifier does
    /// not recognize are kept — the model's list still decides what survives the
    /// worktree filter — but a warn event and a ledger line name them, so a file
    /// that is probably implementation cannot pass for a test in silence.
    /// </summary>
    private async Task<IReadOnlyList<AuthorTestScopeVerdict>> CheckAuthorTestScopeAsync(
        string rootPath,
        string runId,
        string taskId,
        RelayStageDefinition stage,
        IReadOnlyList<string> testFiles,
        RelayConfig config,
        StringBuilder ledger,
        CancellationToken cancellationToken)
    {
        var verdicts = AuthorTestScope.Classify(testFiles, config);
        var suspects = verdicts
            .Where(verdict => verdict.Kind == AuthorTestScopeKind.Suspect)
            .Select(verdict => verdict.Path)
            .ToList();
        if (suspects.Count == 0)
            return verdicts;

        await PublishStage5EventAsync("warn", "author_test_scope_suspect", rootPath, runId, taskId, stage,
            new Dictionary<string, string>
            {
                ["files"] = string.Join(',', suspects),
                ["scope"] = AuthorTestScope.Describe(verdicts)
            }, cancellationToken);
        ledger.AppendLine($"> **Scope check (stage 5)**: suspect entries kept: {string.Join(", ", suspects)}.");
        ledger.AppendLine();
        return verdicts;
    }

    /// <summary>
    /// The one line in the ledger that says what the gate established: a proven
    /// red with its exit code and whatever was stripped to get there, or an
    /// unproven gate with the reason it proved nothing.
    /// </summary>
    private static void AppendAuthorTestGateLedger(StringBuilder ledger, AuthorTestGateOutcome outcome)
    {
        var verdict = outcome.Check == AuthorTestCheck.Red
            ? $"red (exit {outcome.ExitCode})"
            : $"unproven ({outcome.Reason})";
        if (outcome.StrippedFiles.Count > 0)
            verdict += $", stripped: {string.Join(", ", outcome.StrippedFiles)}";
        ledger.AppendLine($"> **Author-test gate (stage 5)**: {verdict}.");
        ledger.AppendLine();
    }

    private static void AppendAuthorTestReaskLedger(StringBuilder ledger, string reason)
    {
        ledger.AppendLine($"> **Re-ask (stage 5)**: {reason}.");
        ledger.AppendLine();
    }

    /// <summary>
    /// What attempt 2 is told, appended verbatim to the stage input. Each trigger
    /// states its own fact — the gate's green before anything was stripped, or the
    /// audit's reading of the diff — because a gate that went red must never be
    /// described to the model as having passed. Neither names a language.
    /// </summary>
    /// <param name="reask">The re-ask being made, which decides the wording.</param>
    /// <returns>The message.</returns>
    private static string AuthorTestReaskMessage(AuthorTestReask reask)
    {
        var files = string.Join(", ", reask.Files);
        return string.Equals(reask.Reason, AuthorTestGateOutcome.GreenBeforeImplementation, StringComparison.Ordinal)
            ? "Your new tests passed before any implementation was stripped, and at least one file "
              + $"you listed as a test file can carry implementation: {files}. Either "
              + "the change was implemented inside a test file, or the tests do not exercise the change. "
              + "Remove every implementation change from the test files so the new tests fail against the "
              + "current code, or rewrite the tests so they fail. Do not touch implementation files."
            : "An audit of your test diff reported implementation changes inside the files you listed "
              + $"as tests: {files}. Remove every implementation change from the test files so that only "
              + "tests remain and the new tests fail against the current code; do not touch "
              + "implementation files.";
    }

    private Task PublishAuthorTestReaskAsync(
        string rootPath, string runId, string taskId, RelayStageDefinition stage,
        AuthorTestReask reask, CancellationToken cancellationToken) =>
        PublishStage5EventAsync("info", "author_test_reask", rootPath, runId, taskId, stage,
            new Dictionary<string, string>
            {
                ["reason"] = reask.Reason,
                ["files"] = string.Join(',', reask.Files)
            }, cancellationToken);

    /// <summary>
    /// Handles a re-asked stage that answered with nothing the driver could read.
    /// The first pass's outcome stands, so the first pass's tree must stand too:
    /// the stage was asked to take implementation OUT of its test files, and an
    /// unreadable answer is no licence to leave whatever attempt 2 wrote behind.
    /// The filter runs again over attempt 1's declared list, and the warning says
    /// why there is no second gate record to find.
    /// </summary>
    /// <param name="rootPath">The workspace root.</param>
    /// <param name="runId">The run this belongs to.</param>
    /// <param name="taskId">The task being run.</param>
    /// <param name="config">The repository config, for the tasks directory.</param>
    /// <param name="stage">The stage definition, for the event's stage and tier.</param>
    /// <param name="reask">The re-ask that was made.</param>
    /// <param name="testFiles">Attempt 1's declared test files, normalized.</param>
    /// <param name="ledger">The ledger to write the note to.</param>
    /// <param name="cancellationToken">Cancels the filter.</param>
    private async Task DiscardUnusableReaskAsync(
        string rootPath,
        string runId,
        string taskId,
        RelayConfig config,
        RelayStageDefinition stage,
        AuthorTestReask reask,
        IReadOnlyList<string> testFiles,
        StringBuilder ledger,
        CancellationToken cancellationToken)
    {
        await PublishStage5EventAsync("warn", "author_test_reask", rootPath, runId, taskId, stage,
            new Dictionary<string, string>
            {
                ["reason"] = reask.Reason,
                ["result"] = "invalid"
            }, cancellationToken);

        var filtered = await WorktreeFilter.DiscardNonTestEditsAsync(
            rootPath, testFiles, config.TasksDir, _dependencies.GitInvoker, cancellationToken);
        var discarded = filtered.TrackedDiscarded.Count + filtered.UntrackedDeleted.Count;
        ledger.AppendLine(
            "> **Re-ask (stage 5)**: the answer was unusable; the first result stands, "
            + $"{discarded} file(s) discarded.");
        ledger.AppendLine();
    }

    private Task PublishAuthorTestUnprovenAsync(
        string rootPath, string runId, string taskId, RelayStageDefinition stage,
        string reason, CancellationToken cancellationToken) =>
        PublishStage5EventAsync("warn", "author_test_unproven", rootPath, runId, taskId, stage,
            new Dictionary<string, string> { ["reason"] = reason }, cancellationToken);

    private Task PublishStage5EventAsync(
        string level,
        string eventName,
        string rootPath,
        string runId,
        string taskId,
        RelayStageDefinition stage,
        Dictionary<string, string> data,
        CancellationToken cancellationToken) =>
        _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, level, eventName, runId, rootPath, taskId,
            stage.Number, stage.Tier, Data: data), cancellationToken);
}
