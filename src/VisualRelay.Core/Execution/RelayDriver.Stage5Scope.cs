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
    /// What attempt 2 is told, appended verbatim to the stage input. It states
    /// the fact (the tests passed with the implementation still in place) and the
    /// two ways out, without naming a language or a framework.
    /// </summary>
    /// <param name="files">The declared test files that can carry implementation.</param>
    /// <returns>The message.</returns>
    private static string AuthorTestReaskMessage(IReadOnlyList<string> files) =>
        "Your new tests passed before any implementation was stripped, and at least one file "
        + $"you listed as a test file can carry implementation: {string.Join(", ", files)}. Either "
        + "the change was implemented inside a test file, or the tests do not exercise the change. "
        + "Remove every implementation change from the test files so the new tests fail against the "
        + "current code, or rewrite the tests so they fail. Do not touch implementation files.";

    private Task PublishAuthorTestReaskAsync(
        string rootPath, string runId, string taskId, RelayStageDefinition stage,
        AuthorTestReask reask, CancellationToken cancellationToken) =>
        PublishStage5EventAsync("info", "author_test_reask", rootPath, runId, taskId, stage,
            new Dictionary<string, string>
            {
                ["reason"] = reask.Reason,
                ["files"] = string.Join(',', reask.Files)
            }, cancellationToken);

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
