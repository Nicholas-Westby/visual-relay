using System.Text;
using System.Text.Json;
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
            rootPath, testFiles, config.TasksDir, cancellationToken);
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
            var gateResult = await AuthorTestGate.RunAsync(rootPath, taskId, runId, manifest, testFiles, command, _dependencies.TestRunner, cancellationToken);
            if (gateResult.Error is not null)
                return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, gateResult.Error, null, statusEntries, cancellationToken), null, null);

            if (gateResult.RestoreResult == RedGateRestoreResult.Conflict)
                return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5, "red gate stash restore conflict", null, statusEntries, cancellationToken), null, null);

            var testResult = gateResult.TestResult;
            var duration = testResult.Elapsed.TotalSeconds;
            if (testResult.TimedOut)
                return new Stage5Result(await FlagAsync(rootPath, runId, taskId, taskDirectory, 5,
                    ErrorHintClassifier.WithHint(testResult.Output), null, statusEntries, cancellationToken), null, null);

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
}
