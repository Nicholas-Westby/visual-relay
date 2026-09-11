using System.Text;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Result of one stage-5 pass: the filter, the manifest merge, the scope
    /// check and one gate run.
    /// </summary>
    /// <param name="Outcome">Non-null when the pass flags (stop the pipeline).</param>
    /// <param name="Gate">What the gate established, or null when flagged.</param>
    /// <param name="TestDurationSeconds">How long the gate command ran, or null when nothing ran.</param>
    /// <param name="Reask">The single re-ask this pass asks for, or null for none.</param>
    /// <param name="TestFiles">What the pass declared as its test files, normalized.</param>
    private readonly record struct Stage5Result(
        RelayTaskOutcome? Outcome,
        AuthorTestGateOutcome? Gate,
        double? TestDurationSeconds,
        AuthorTestReask? Reask = null,
        IReadOnlyList<string>? TestFiles = null);

    /// <summary>What stage 5 leaves behind for the stage loop.</summary>
    /// <param name="Outcome">Non-null when the stage flags.</param>
    /// <param name="Body">The stage body to record, replaced by the re-ask's answer when there was one.</param>
    /// <param name="Check">"red" or "unproven".</param>
    /// <param name="Reason">Why the check reads as it does, or null.</param>
    /// <param name="TestDurationSeconds">How long the last gate command ran.</param>
    /// <param name="ImplementationFrontLoaded">True when the fix is already in the tree.</param>
    private sealed record Stage5StageResult(
        RelayTaskOutcome? Outcome,
        string Body,
        string? Check,
        string? Reason,
        double? TestDurationSeconds,
        bool ImplementationFrontLoaded);

    /// <summary>
    /// Runs stage 5's post-processing and, at most once per run, re-asks the
    /// stage when its tests passed with the implementation still in place. The
    /// re-ask is an ordinary second stage attempt (same invocation builder, same
    /// attempt numbering) carrying one extra instruction, and its result is put
    /// through the same filter, merge, scope check and gate.
    /// </summary>
    private async Task<Stage5StageResult> RunStage5WithReaskAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        List<string> manifest,
        StringBuilder ledger,
        List<StageStatusEntry> statusEntries,
        JsonElement json,
        string body,
        string reportFile,
        bool implementationFrontLoaded,
        CancellationToken cancellationToken)
    {
        // The attempt the gate reports is the attempt the stage actually ran, read
        // off the invocation's own report file, so the verify_result and the
        // persisted gate output cannot drift from the stage's numbering.
        var pass = await HandleStage5Async(rootPath, runId, taskId, taskDirectory, config, stage,
            manifest, ledger, statusEntries, json, AttemptOf(reportFile), reaskUsed: false, cancellationToken);

        if (pass is { Outcome: null, Reask: { } request })
        {
            var reask = await ReaskAuthorTestsAsync(rootPath, runId, taskId, taskDirectory, config,
                stage, input, ledger, manifest, request, cancellationToken);
            if (reask.Contract is { } second)
            {
                body = reask.Body;
                pass = await HandleStage5Async(rootPath, runId, taskId, taskDirectory, config, stage,
                    manifest, ledger, statusEntries, second, reask.Attempt, reaskUsed: true, cancellationToken);
            }
            else
                // Attempt 1's outcome stands, so attempt 2's edits must not: it was
                // asked to take implementation OUT of the test files and answered
                // with nothing readable, which is no licence to leave code behind.
                await DiscardUnusableReaskAsync(rootPath, runId, taskId, config, stage, request,
                    pass.TestFiles ?? [], ledger, cancellationToken);
        }

        if (pass.Outcome is not null)
            return new Stage5StageResult(pass.Outcome, body, null, null, null, implementationFrontLoaded);

        if (pass.Gate is { Check: AuthorTestCheck.Unproven, Reason: { } reason })
            await PublishAuthorTestUnprovenAsync(rootPath, runId, taskId, stage, reason, cancellationToken);

        return new Stage5StageResult(null, body, pass.Gate?.CheckName, pass.Gate?.Reason,
            pass.TestDurationSeconds,
            await RecheckEarlyImplementationAsync(
                rootPath, config, manifest, implementationFrontLoaded, cancellationToken));
    }

    /// <summary>
    /// Handle one stage-5 pass: discard non-test edits, merge testFiles into the
    /// manifest, classify them, and run the author-test gate over the result.
    /// </summary>
    private async Task<Stage5Result> HandleStage5Async(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        List<string> manifest,
        StringBuilder ledger,
        List<StageStatusEntry> statusEntries,
        JsonElement json,
        int attempt,
        bool reaskUsed,
        CancellationToken cancellationToken)
    {
        // One spelling for the whole stage. The filter has always normalized the
        // list it is handed ("+src/a.rs" is the Plan stage's new-file marker,
        // "./src/a.rs" is a model habit), so the scope check, the strip set, the
        // targeted command and the audit have to see the same paths it does —
        // otherwise a file the filter keeps is classified suspect, gated under a
        // name no command can resolve, and audited under a third.
        var testFiles = WorktreeFilter.NormalizeTestFileList(ReadStringArray(json, "testFiles"));

        // ── Step 1: Discard all non-testFiles edits ──────────────────
        // WorktreeFilter reverts tracked production-file changes to HEAD
        // and deletes untracked files not listed in testFiles. The
        // red-gate then strips manifest impl files, runs the test command
        // (compile failures count as red), and restores them. Stage 6
        // starts with a clean base: only test edits present.
        var filterResult = await WorktreeFilter.DiscardNonTestEditsAsync(
            rootPath, testFiles, config.TasksDir, _dependencies.GitInvoker, cancellationToken);

        // Record the ledger note BEFORE the error check so the discarded
        // inventory is captured even when an Error causes a flag.
        if (filterResult.TrackedDiscarded.Count > 0 || filterResult.UntrackedDeleted.Count > 0)
        {
            var parts = new List<string>();
            if (filterResult.TrackedDiscarded.Count > 0)
                parts.Add($"tracked reverted: {filterResult.TrackedDiscarded.Count}");
            if (filterResult.UntrackedDeleted.Count > 0)
                parts.Add($"untracked deleted: {filterResult.UntrackedDeleted.Count}");
            ledger.AppendLine($"> **Worktree filter (stage 5)**: discarded {string.Join(", ", parts)}.");
            ledger.AppendLine();
        }

        if (filterResult.Error is not null)
        {
            return new Stage5Result(
                await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                    $"worktree filter failed: {filterResult.Error}", null,
                    statusEntries, cancellationToken),
                null, null);
        }

        // ── Step 2: Merge testFiles into manifest ────────────────────
        var testFilesAdded = 0;
        foreach (var tf in testFiles)
        {
            if (!manifest.Contains(tf, StringComparer.Ordinal))
            {
                if (IsPathUnderDirectory(rootPath, tf, config.TasksDir))
                {
                    ledger.AppendLine($"> **Note**: dropped task-dir testFile `{tf}` from manifest merge.");
                    ledger.AppendLine();
                }
                else
                {
                    manifest.Add(tf);
                    testFilesAdded++;
                }
            }
        }
        if (testFilesAdded > 0)
        {
            await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
            ledger.AppendLine($"> **Manifest merge (stage 5)**: added {testFilesAdded} authored test file(s).");
            ledger.AppendLine();
        }

        // ── Step 3: Scope check, the diff audit, then the gate ───────
        var verdicts = await CheckAuthorTestScopeAsync(
            rootPath, runId, taskId, stage, testFiles, config, ledger, cancellationToken);
        // The audit can only ask for the one re-ask, so on the pass that already
        // follows one it has nothing left to change and is not worth its call.
        var audit = reaskUsed
            ? default
            : await AuditAuthorTestDiffAsync(
                rootPath, runId, taskId, config, testFiles, verdicts, ledger, cancellationToken);
        var (outcome, result) = await RunAuthorTestGateAsync(rootPath, runId, taskId, stage,
            config, manifest, testFiles, verdicts, reaskUsed, cancellationToken);

        if (outcome.Check is null)
            return new Stage5Result(
                await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, outcome.Reason!, null,
                    statusEntries, cancellationToken),
                null, null, null, testFiles);

        await PublishAuthorTestVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage,
            attempt, config, outcome, result, manifest, cancellationToken);
        AppendAuthorTestGateLedger(ledger, outcome);
        return new Stage5Result(null, outcome, result?.Elapsed.TotalSeconds,
            ResolveAuthorTestReask(outcome, audit, testFiles), testFiles);
    }

    /// <summary>
    /// Runs stage 5 once more with the re-ask appended to its input, using the
    /// same invocation builder every stage retry uses (so the attempt index, the
    /// trace directory and the report file follow the normal numbering).
    /// </summary>
    private async Task<(string Body, JsonElement? Contract, int Attempt)>
        ReaskAuthorTestsAsync(
            string rootPath,
            string runId,
            string taskId,
            string taskDirectory,
            RelayConfig config,
            RelayStageDefinition stage,
            RelayTaskInput input,
            StringBuilder ledger,
            IReadOnlyList<string> manifest,
            AuthorTestReask reask,
            CancellationToken cancellationToken)
    {
        await PublishAuthorTestReaskAsync(
            rootPath, runId, taskId, stage, reask, cancellationToken);
        AppendAuthorTestReaskLedger(ledger, reask.Reason);

        var reasked = input with
        {
            Markdown = input.Markdown + Environment.NewLine + Environment.NewLine
                + AuthorTestReaskMessage(reask)
        };
        var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, stage,
            reasked, ledger, manifest);
        var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
        // No cost is carried out of here: the stage is re-priced from every report
        // it left once the pass is over, which is the same rule the archive uses.
        var attempt = AttemptOf(invocation.ReportFile);
        return result.IsValid && TryParseContractJson(result.Json, out var contract, out _)
            ? (result.Json!, contract, attempt)
            : (string.Empty, null, attempt);
    }

    /// <summary>
    /// Re-check whether implementation is already underway after stage 5 ran.
    /// WorktreeFilter inside <see cref="HandleStage5Async"/> may have reverted
    /// premature non-test edits back to HEAD, so the implementation may no longer
    /// be in the working tree — stage 6 should use the normal Implement prompt.
    /// </summary>
    private async Task<bool> RecheckEarlyImplementationAsync(
        string rootPath,
        RelayConfig config,
        IReadOnlyList<string> manifest,
        bool currentValue,
        CancellationToken cancellationToken)
    {
        if (!config.DownshiftOnEarlyImplementation)
            return currentValue;
        return await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            rootPath, manifest, IsImpl, _dependencies.GitInvoker, cancellationToken, isTestFile: f => TestPathClassifier.IsTestRelated(f, config.TestPaths));
    }
}
