using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Persists the FULL untrimmed verify output to a per-attempt artifact and emits a
    /// structured <c>verify_result</c> event carrying the command, exit code, verdict,
    /// distilled reason, working-tree hash, and a POINTER to that artifact — never the
    /// full output inline. Mirrors <c>TryPersistKilledOutput</c>'s file convention so the
    /// autopsy trail is uniform. Called at BOTH authoritative gate runs (stage 10 and the
    /// stage-11 loop) so every red is observable after the fact (R5).
    /// NOTE: the event reports the RAW authoritative-gate verdict; at stage 10 a task can
    /// still go green via baseline-exclusion of pre-existing failures, so a green task
    /// legitimately having a <c>check:"red"</c> stage-9 <c>verify_result</c> is not a contradiction.
    /// Returns a tuple of (output-file-path, check, tree-hash, distilled-reason) so
    /// callers can embed the verify signature into flag reasons without re-computing.
    /// </summary>
    private async Task<(string? OutputFile, string Check, string TreeHash, string Reason)> PublishVerifyResultAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition stage, int attempt, RelayConfig config,
        TestRunResult testResult, IReadOnlyList<string> manifest,
        CancellationToken cancellationToken, string? overrideCheck = null,
        // The COMPLETE combined failure log (full test output PLUS any guard / bootstrap /
        // new-guard text) to persist as the artifact, so the file is the full version of the
        // trimmed tail the fix-verify prompt shows it. Null (the default) persists
        // testResult.Output — the right content for a green gate, or any caller whose only
        // failure source is the test command itself.
        string? combinedFailureOutput = null,
        SetupCheckResults? setupChecks = null)
    {
        var check = overrideCheck ?? (testResult.ExitCode == 0 ? "green" : "red");
        var reason = testResult.ExitCode != 0
            ? SwivalSubagentRunner.ExtractFailureReason(testResult.Output)
            : BuildSetupCheckFailureReason(setupChecks);
        // NOTE: WorkingTreeHash fingerprints only the manifest files' contents — a coarse
        // signal, acceptable for observability (and for the Task 2 convergence guard).
        var treeHash = WorkingTreeHash(rootPath, manifest);
        // Persist the SAME source the in-prompt tail is derived from so the file is genuinely
        // "the complete log" the prompt points at — including guard/bootstrap text when those
        // are the failure (the test command alone may have passed).
        var outputFile = TryPersistVerifyOutput(
            taskDirectory, stage.Number, attempt, check, combinedFailureOutput ?? testResult.Output);

        // Persist structured per-check JSON artifact alongside the verify output.
        if (setupChecks is not null)
            TryPersistVerifyChecksJson(taskDirectory, stage.Number, attempt, setupChecks);

        var data = new Dictionary<string, string>
        {
            ["command"] = config.TestCommand,
            ["exitCode"] = testResult.ExitCode.ToString(),
            ["check"] = check,
            ["reason"] = reason,
            ["treeHash"] = treeHash,
            ["outputFile"] = outputFile ?? string.Empty
        };
        if (setupChecks is not null)
        {
            foreach (var (k, v) in setupChecks.ToEventData())
                data[k] = v;
        }

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "verify_result", runId, rootPath, taskId,
            stage.Number, stage.Tier, Attempt: attempt,
            Data: data), cancellationToken);

        return (outputFile, check, treeHash, reason);
    }

    /// <summary>
    /// Writes the verify run's full output to
    /// <c>stage{N}-attempt{M}.verify-output.txt</c> under the task directory, returning
    /// the ABSOLUTE path (or null on failure). Mirrors <c>TryPersistKilledOutput</c>.
    /// The path is run through <see cref="Path.GetFullPath(string)"/> so it is absolute
    /// regardless of how the root was passed — the path is handed to the next Fix-verify
    /// agent, which reads it under the sandbox's <c>--allow-cwd</c> grant where a relative
    /// path would not resolve.
    /// </summary>
    internal static string? TryPersistVerifyOutput(
        string taskDirectory, int stageNum, int attempt, string check, string output)
    {
        try
        {
            var path = Path.GetFullPath(
                Path.Combine(taskDirectory, $"stage{stageNum}-attempt{attempt}.verify-output.txt"));
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

    /// <summary>
    /// Writes the structured per-check breakdown to
    /// <c>stage{N}-attempt{M}.verify-checks.json</c> under the task directory.
    /// Best-effort mirror of <see cref="TryPersistVerifyOutput"/>: the path is not
    /// consumed by anything (unlike the verify-output path, which rides the
    /// verify_result event), so a persistence failure is swallowed, never failing
    /// the verify.
    /// </summary>
    private static void TryPersistVerifyChecksJson(
        string taskDirectory, int stageNum, int attempt, SetupCheckResults setupChecks)
    {
        try
        {
            var path = Path.GetFullPath(
                Path.Combine(taskDirectory, $"stage{stageNum}-attempt{attempt}.verify-checks.json"));
            var json = JsonSerializer.Serialize(setupChecks,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort artifact — a write failure must not fail the verify.
        }
    }

    /// <summary>
    /// Builds a distilled failure reason naming which setup check failed, its
    /// command, and the first sandbox denial path when available.
    /// Format: <c>setup check failure: {check} '{command}' (sandbox denial: {path})</c>.
    /// Truncation-safe: the reason is kept within the 200-char flag-reason limit so
    /// the check name and command survive even when the denial path is long.
    /// </summary>
    internal static string BuildSetupCheckFailureReason(SetupCheckResults? setupChecks)
    {
        if (setupChecks is null)
            return "setup check failure";

        if (!setupChecks.IsAnyRed())
            return string.Empty;

        // Bootstrap is checked first in the fix-verify loop, so bootstrap
        // failures take priority over guard failures.
        if (setupChecks.BootstrapCheck == "red")
        {
            var cmd = setupChecks.BootstrapCommand ?? "bootstrap command";
            if (setupChecks.BootstrapDenials is { Count: > 0 })
                return BuildDenialReason("bootstrap", cmd, setupChecks.BootstrapDenials[0].Target);
            return $"setup check failure: bootstrap '{cmd}'";
        }

        if (setupChecks.GuardCheck == "red")
        {
            var cmd = setupChecks.GuardCommand ?? "guard command";
            if (setupChecks.GuardDenials is { Count: > 0 })
                return BuildDenialReason("guard", cmd, setupChecks.GuardDenials[0].Target);
            return $"setup check failure: guard '{cmd}'";
        }

        return string.Empty;
    }

    private static string BuildDenialReason(string checkName, string command, string targetPath)
    {
        // Keep the reason within 200 chars so flag-reason truncation in VerifyFix
        // (first-line capped at 200) still captures the check name, command, and
        // enough of the denial path to be actionable.
        var prefix = $"setup check failure: {checkName} '{command}' (sandbox denial: ";
        const int maxLen = 200;
        var suffix = ")";
        var overhead = prefix.Length + suffix.Length;
        if (overhead >= maxLen)
            return $"setup check failure: {checkName} '{command}'";

        var maxTarget = maxLen - overhead;
        if (targetPath.Length <= maxTarget)
            return prefix + targetPath + suffix;

        return prefix + targetPath[..(maxTarget - 1)] + "…" + suffix;
    }
}
