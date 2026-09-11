using System.Text;
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
    /// <param name="CostDelta">USD the call added.</param>
    /// <param name="UnknownCostDelta">1 when the call ran but priced nothing.</param>
    private readonly record struct AuthorTestAuditPass(
        bool ReaskRequested, double CostDelta, int UnknownCostDelta);

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
    /// <returns>What the audit asks of the pass, and what it cost.</returns>
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

        // Priced exactly like the re-ask: the report the call wrote is the only
        // place its cost exists, and the stage loop folds the delta into the
        // session total. A call that never reached the model wrote no report and
        // is not an unpriced run either.
        var cost = TryEstimateCost(AuthorTestDiffAuditor.ReportFile(rootPath, taskId));
        return new AuthorTestAuditPass(
            result.ImplementationHunks.Count > 0,
            cost?.CostUsd ?? 0,
            cost is null && result.Error is null ? 1 : 0);
    }

    /// <summary>
    /// The re-ask this pass asks for, or null. The gate and the audit can both
    /// want one and there is still only one — the gate's, because it knows which
    /// files could carry the implementation it just failed to strip. A pass that
    /// already follows a re-ask never asks for another.
    /// </summary>
    /// <param name="outcome">What the gate established.</param>
    /// <param name="audit">What the audit established.</param>
    /// <param name="testFiles">The audited files, which the audit's message names.</param>
    /// <param name="reaskUsed">True when this pass already follows a re-ask.</param>
    /// <returns>The re-ask to perform, or null.</returns>
    private static AuthorTestReask? ResolveAuthorTestReask(
        AuthorTestGateOutcome outcome,
        AuthorTestAuditPass audit,
        IReadOnlyList<string> testFiles,
        bool reaskUsed)
    {
        if (outcome.ReaskRequested)
            return new AuthorTestReask(
                AuthorTestGateOutcome.InlineOrSuspect(outcome.Verdicts),
                AuthorTestGateOutcome.GreenBeforeImplementation);

        return audit.ReaskRequested && !reaskUsed
            ? new AuthorTestReask(testFiles, AuthorTestDiffAuditor.ReaskReason)
            : null;
    }

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
