using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Persists the FULL untrimmed verify output to a per-attempt artifact and emits a
    /// structured <c>verify_result</c> event carrying the command, exit code, verdict,
    /// distilled reason, working-tree hash, and a POINTER to that artifact — never the
    /// full output inline. Mirrors <c>TryPersistKilledOutput</c>'s file convention so the
    /// autopsy trail is uniform. Called at BOTH authoritative gate runs (stage 9 and the
    /// stage-10 loop) so every red is observable after the fact (R5).
    /// NOTE: the event reports the RAW authoritative-gate verdict; at stage 9 a task can
    /// still go green via baseline-exclusion of pre-existing failures, so a green task
    /// legitimately having a <c>check:"red"</c> stage-9 <c>verify_result</c> is not a contradiction.
    /// Returns the persisted full-output artifact PATH (or null when the write failed) so the
    /// caller can hand it to the next Fix-verify agent prompt (read-the-complete-log breadcrumb).
    /// </summary>
    private async Task<string?> PublishVerifyResultAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition stage, int attempt, RelayConfig config,
        TestRunResult testResult, IReadOnlyList<string> manifest,
        CancellationToken cancellationToken, string? overrideCheck = null)
    {
        var check = overrideCheck ?? (testResult.ExitCode == 0 ? "green" : "red");
        var reason = testResult.ExitCode != 0
            ? SwivalSubagentRunner.ExtractFailureReason(testResult.Output)
            : string.Empty;
        // NOTE: WorkingTreeHash fingerprints only the manifest files' contents — a coarse
        // signal, acceptable for observability (and for the Task 2 convergence guard).
        var treeHash = WorkingTreeHash(rootPath, manifest);
        var outputFile = TryPersistVerifyOutput(taskDirectory, stage.Number, attempt, check, testResult.Output);

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "verify_result", runId, rootPath, taskId,
            stage.Number, stage.Tier, Attempt: attempt,
            Data: new Dictionary<string, string>
            {
                ["command"] = config.TestCommand,
                ["exitCode"] = testResult.ExitCode.ToString(),
                ["check"] = check,
                ["reason"] = reason,
                ["treeHash"] = treeHash,
                ["outputFile"] = outputFile ?? string.Empty
            }), cancellationToken);

        return outputFile;
    }

    /// <summary>
    /// Writes the verify run's full output to
    /// <c>stage{N}-attempt{M}.verify-output.txt</c> under the task directory, returning
    /// the path (or null on failure). Mirrors <c>TryPersistKilledOutput</c>.
    /// </summary>
    private static string? TryPersistVerifyOutput(
        string taskDirectory, int stageNum, int attempt, string check, string output)
    {
        try
        {
            var path = Path.Combine(taskDirectory, $"stage{stageNum}-attempt{attempt}.verify-output.txt");
            var header =
                $"# verify output (autopsy artifact){Environment.NewLine}" +
                $"# check: {check}{Environment.NewLine}" +
                $"# capturedUtc: {DateTimeOffset.UtcNow:O}  bytes: {output.Length}{Environment.NewLine}{Environment.NewLine}";
            File.WriteAllText(path, header + output);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
