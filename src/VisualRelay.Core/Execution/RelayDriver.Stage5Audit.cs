using System.Text;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// The cheap-tier diff audit behind authorTests.diffAudit: a second opinion on
// what the Author-tests stage actually wrote, and the single re-ask a reported
// hunk buys. The audit decides nothing else.
public sealed partial class RelayDriver
{
    /// <summary>The single re-ask a stage-5 pass asks for.</summary>
    /// <param name="Files">The files the message names.</param>
    /// <param name="Reason">Why it was asked, for the event and the ledger.</param>
    private sealed record AuthorTestReask(IReadOnlyList<string> Files, string Reason);

    /// <summary>What one pass's audit left behind.</summary>
    /// <param name="ReaskRequested">True when the audit reported at least one hunk.</param>
    private readonly record struct AuthorTestAuditPass(bool ReaskRequested);

    /// <summary>
    /// Audits the stage's own diff when the config asks for it. The answer is
    /// advisory by construction: it can ask for the one re-ask and nothing more,
    /// so a failed call, an unreadable answer or a model that reports nothing all
    /// leave the pass exactly as they found it.
    /// </summary>
    /// <param name="rootPath">The workspace root.</param>
    /// <param name="runId">The run the call belongs to.</param>
    /// <param name="taskId">The task being run.</param>
    /// <param name="config">The repository config, for the mode and the budget.</param>
    /// <param name="testFiles">What the stage declared as its test files.</param>
    /// <param name="verdicts">How those files were classified.</param>
    /// <param name="ledger">The ledger the result is written to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the audit asks of the pass.</returns>
    private async Task<AuthorTestAuditPass> AuditAuthorTestDiffAsync(
        string rootPath,
        string runId,
        string taskId,
        RelayConfig config,
        IReadOnlyList<string> testFiles,
        IReadOnlyList<AuthorTestScopeVerdict> verdicts,
        StringBuilder ledger,
        CancellationToken cancellationToken)
    {
        if (!AuthorTestDiffAuditor.ShouldRun(config.AuthorTests.DiffAudit, verdicts))
            return default;

        var result = await AuthorTestDiffAuditor.RunAsync(rootPath, taskId, runId, testFiles, config,
            _dependencies.SubagentRunner, _dependencies.GitInvoker, _dependencies.EventSink,
            cancellationToken);
        AppendAuthorTestAuditLedger(ledger, result);
        // The call's cost needs no carrying: its report sits beside the stage's own
        // attempts and the stage is re-priced from all of them once the pass ends.
        return new AuthorTestAuditPass(result.ImplementationHunks.Count > 0);
    }

    /// <summary>
    /// The re-ask this pass asks for, or null. The gate and the audit can both
    /// want one and there is still only one — the gate's, because it knows which
    /// files could carry the implementation it just failed to strip. A pass that
    /// already follows a re-ask never asks for another.
    /// </summary>
    /// <param name="outcome">What the gate established.</param>
    /// <param name="audit">What the audit established, if it ran at all.</param>
    /// <param name="testFiles">The audited files, which the audit's message names.</param>
    /// <returns>The re-ask to perform, or null.</returns>
    private static AuthorTestReask? ResolveAuthorTestReask(
        AuthorTestGateOutcome outcome,
        AuthorTestAuditPass audit,
        IReadOnlyList<string> testFiles)
    {
        if (outcome.ReaskRequested)
            return new AuthorTestReask(
                AuthorTestGateOutcome.InlineOrSuspect(outcome.Verdicts),
                AuthorTestGateOutcome.GreenBeforeImplementation);

        // The audit does not run on the pass that already follows a re-ask, so this
        // can only ever be the first one.
        return audit.ReaskRequested
            ? new AuthorTestReask(testFiles, AuthorTestDiffAuditor.ReaskReason)
            : null;
    }

    /// <summary>
    /// Re-prices stage 5 once its re-ask and its audit have written their reports.
    /// The loop's own sweep runs before both, so the stage entry, the
    /// <c>stage_done</c> event and the session total would otherwise carry only the
    /// first attempt. Re-reading every report keeps one rule for the whole stage,
    /// which is also what the archived squash and a resumed run rebuild from.
    /// </summary>
    /// <param name="taskDirectory">Where the stage's reports are.</param>
    /// <param name="swept">What the loop's own sweep priced, before the extra calls.</param>
    /// <returns>The entry to record, and what the session total still owes.</returns>
    private static (RelayCostEstimate? Cost, double SessionDelta, int UnknownDelta) RepriceStage5(
        string taskDirectory, RelayCostEstimate? swept)
    {
        var repriced = EstimateStageCostCumulative(taskDirectory, 5);
        // The loop already added what the sweep saw and, when it saw nothing,
        // already counted the stage as unpriced; only the difference is new.
        return repriced is null
            ? (swept, 0, 0)
            : (repriced, repriced.CostUsd - (swept?.CostUsd ?? 0), swept is null ? -1 : 0);
    }

    /// <summary>The attempt a stage's report file belongs to, or 1 when unreadable.</summary>
    /// <param name="reportFile">The invocation's report file.</param>
    /// <returns>The attempt index.</returns>
    private static int AttemptOf(string reportFile) =>
        RelayAttempt.TryParse(Path.GetFileName(reportFile), out _, out var attempt) ? attempt : 1;

    /// <summary>
    /// The one line in the ledger that says what the audit read in the diff: the
    /// hunks it reported, that it reported none, or that it could not answer.
    /// </summary>
    /// <param name="ledger">The ledger to append to.</param>
    /// <param name="result">What the audit answered.</param>
    private static void AppendAuthorTestAuditLedger(StringBuilder ledger, AuthorTestAuditResult result)
    {
        var verdict = result switch
        {
            { Error: { } error } => $"not run: {error}",
            { ImplementationHunks.Count: 0 } => "no implementation hunks reported",
            _ => $"{result.ImplementationHunks.Count} implementation hunk(s) reported: "
                + string.Join(", ", result.ImplementationHunks.Select(hunk => hunk.File))
        };
        ledger.AppendLine($"> **Diff audit (stage 5)**: {verdict}.");
        ledger.AppendLine();
    }
}
