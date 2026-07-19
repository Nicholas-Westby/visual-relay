using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Result returned by <see cref="HandleStage5Async"/>.
    /// </summary>
    /// <param name="Outcome">Non-null when the stage flags (stop the pipeline).</param>
    /// <param name="Check">The stage check result ("red" or "green"), or null if flagged.</param>
    /// <param name="TestDurationSeconds">Test duration, or null.</param>
    internal readonly record struct Stage5Result(
        RelayTaskOutcome? Outcome, string? Check, double? TestDurationSeconds);

    /// <summary>
    /// Handle stage 5 (Author-tests): discard non-test edits, merge testFiles
    /// into the manifest, and run the red-gate to confirm tests fail without
    /// implementation. Returns a result with the outcome (non-null = flag),
    /// check string, and test duration.
    /// </summary>
    private async Task<Stage5Result> HandleStage5Async(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        List<string> manifest,
        StringBuilder ledger,
        List<StageStatusEntry> statusEntries,
        JsonElement json,
        CancellationToken cancellationToken)
    {
        var testFiles = ReadStringArray(json, "testFiles");

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

        var hasImpl = manifest.Any(f => !testFiles.Contains(f, StringComparer.Ordinal) && IsImpl(f));

        if (hasImpl)
        {
            var command = config.TestFileCommand.Replace("{files}", string.Join(' ', testFiles), StringComparison.Ordinal);
            var gateResult = await AuthorTestGate.RunAsync(rootPath, taskId, runId, manifest, testFiles, command, _dependencies.TestRunner, _dependencies.GitInvoker, cancellationToken);
            if (gateResult.Error is not null)
                return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, gateResult.Error, null, statusEntries, cancellationToken), null, null);

            if (gateResult.RestoreResult == RedGateRestoreResult.Conflict)
                return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, "red gate stash restore conflict", null, statusEntries, cancellationToken), null, null);

            var testResult = gateResult.TestResult;
            var duration = testResult.Elapsed.TotalSeconds;
            if (testResult.TimedOut)
                return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                    ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken), null, null);

            // ── Gate-unusability detection ──────────────────────────
            // Exit code 127 = command not found (runner can't start).
            // "no tests found/collected" = zero tests ran — not a real red.
            // In either case the red gate is infrastructure-broken, not
            // correctly-red. Emit a warn event and skip the pass/fail
            // assertion rather than passing vacuously.
            if (IsGateUnusable(testResult))
            {
                var tail = testResult.Output.Length > 200
                    ? testResult.Output[^200..]
                    : testResult.Output;
                await _dependencies.EventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "warn", "author_test_gate_unusable", runId,
                    rootPath, taskId, Data: new Dictionary<string, string>
                    {
                        ["command"] = command,
                        ["exitCode"] = testResult.ExitCode.ToString(),
                        ["outputTail"] = tail
                    }), cancellationToken);
                return new Stage5Result(null, null, null);
            }

            var check = testResult.ExitCode == 0 ? "green" : "red";
            if (check != "red")
            {
                if (gateResult.StashedImplementation)
                    return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                        "author-tests passed after implementation files were stripped", null, statusEntries, cancellationToken), null, null);

                check = "green"; // already-resolved: no impl delta
                ledger.AppendLine("> **Already-resolved**: no implementation delta to strip; accepted green regression coverage.");
                ledger.AppendLine();
            }

            return new Stage5Result(null, check, duration);
        }

        return new Stage5Result(null, null, null);
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

    /// <summary>
    /// Zero-tests pattern: "0 tests" / "0 tests collected" / "ran 0 tests"
    /// but NOT "10 tests" / "230 tests" / "Ran 100 tests".  The regex
    /// requires the zero to be a standalone number (not preceded by another
    /// digit).
    /// </summary>
    private static readonly Regex ZeroTestsPattern =
        new(@"(?<!\d)0\s+tests",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the test runner could not execute meaningfully (command
    /// not found, or zero tests collected), making the red-gate assertion
    /// untrustworthy. Avoids silently passing a gate whose infrastructure is
    /// broken — independent of any specific toolchain.
    /// </summary>
    private static bool IsGateUnusable(TestRunResult result)
    {
        // Exit code 127 = command not found (POSIX convention; also followed
        // by many shells and process runners on non-POSIX platforms).
        if (result.ExitCode == 127)
            return true;

        // Zero-tests-collected patterns produced by common runners when the
        // command can start but finds no tests to execute. The heuristic is
        // intentionally loose (case-insensitive) — a false positive here
        // skips the gate conservatively rather than passing it vacuously.
        var output = result.Output;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains("no tests found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no tests collected", StringComparison.OrdinalIgnoreCase)
            || ZeroTestsPattern.IsMatch(output)
            || output.Contains("zero tests", StringComparison.OrdinalIgnoreCase);
    }
}
