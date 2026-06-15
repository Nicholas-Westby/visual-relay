using System.Text;
using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Runs the guard command on the working tree. When the guard fails and
    /// <paramref name="baselineVerify"/> is true, stashes changes, runs the
    /// guard against the clean baseline, and diffs output lines. Only lines
    /// NOT present in the baseline are returned as new violations;
    /// pre-existing violations are noted but don't block the commit.
    /// When <paramref name="baselineVerify"/> is false, every guard failure
    /// is treated as new.
    /// </summary>
    /// <returns>
    /// (<paramref name="newViolations"/>, <paramref name="fullOutput"/>,
    ///  <paramref name="timedOut"/>).
    /// <paramref name="newViolations"/> is null when there are no new
    /// violations (guard passed or only pre-existing debt).
    /// <paramref name="fullOutput"/> is the complete guard stdout/stderr.
    /// <paramref name="timedOut"/> is true when the guard command timed out
    /// — callers must flag, not enter fix-verify.
    /// </returns>
    private static async Task<(string? NewViolations, string? FullOutput, bool TimedOut)> RunGuardCheckAsync(
        string rootPath,
        string taskId,
        string runId,
        ITestRunner testRunner,
        string? formatCmd,
        string guardCmd,
        bool baselineVerify,
        CancellationToken ct)
    {
        // Auto-format the working tree before the guard check so format-only
        // violations never cause a Fix-verify loop.
        if (!string.IsNullOrWhiteSpace(formatCmd))
            await testRunner.RunAsync(rootPath, formatCmd, ct);

        var workingResult = await testRunner.RunAsync(rootPath, guardCmd, ct);
        var workingOutput = workingResult.Output ?? string.Empty;

        // Guard timed out — caller must flag, not enter fix-verify.
        if (workingResult.TimedOut)
            return (workingOutput, workingOutput, true);

        // Guard passed — nothing to report.
        if (workingResult.ExitCode == 0)
            return (null, null, false);

        // No baseline verify — all output is new.
        if (!baselineVerify)
            return (workingOutput, workingOutput, false);

        // Stash working changes, run guard on clean tree, diff.
        var tag = RedGate.StashTag(taskId, runId);
        var stashed = await RedGate.StashAllAsync(rootPath, tag, ct);
        try
        {
            if (!stashed)
                return (workingOutput, workingOutput, false); // can't baseline — treat all as new

            var baselineResult = await testRunner.RunAsync(rootPath, guardCmd, ct);
            var baselineOutput = baselineResult.Output ?? string.Empty;

            if (baselineResult.TimedOut)
                return (workingOutput, workingOutput, false); // can't baseline — treat all as new

            // Diff working vs baseline using count-normalized keys so that
            // pre-existing oversize files the task merely touched (shifting
            // their line count, e.g. 332→333) are NOT classified as new
            // violations.  Only genuinely-new violations (files with no
            // baseline twin, or files pushed over the threshold) surface.
            var baselineKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in OutputLineSet(baselineOutput))
                baselineKeys.Add(NormalizeForComparison(line));

            var newRawLines = new List<string>();
            foreach (var line in OutputLineSet(workingOutput))
            {
                if (!baselineKeys.Contains(NormalizeForComparison(line)))
                    newRawLines.Add(line);
            }

            if (newRawLines.Count == 0)
                return (null, workingOutput, false); // all pre-existing

            return (string.Join('\n', newRawLines.Order(StringComparer.Ordinal)), workingOutput, false);
        }
        finally
        {
            if (stashed && await RedGate.RestoreStashAsync(rootPath, tag, ct)
                == RedGateRestoreResult.Conflict)
            {
                // Baseline restore conflict is non-fatal for guard diff;
                // treat all violations as new.
            }
        }
    }

    /// <summary>
    /// Normalizes a guard output line for baseline comparison by replacing
    /// every run of digits with <c>#</c>.  This prevents the count-drift
    /// footgun where <c>file too large: X has 332 lines (limit 300)</c> vs
    /// <c>... has 333 lines ...</c> would be classified as a NEW violation
    /// just because a pre-existing oversize file was touched.  Only
    /// genuinely-new violations (files with no baseline twin, or files
    /// pushed over the threshold) surface.
    /// </summary>
    internal static string NormalizeForComparison(string line)
    {
        return Regex.Replace(line, @"\d+", "#");
    }

    /// <summary>
    /// Splits output into trimmed, non-empty lines in a case-sensitive set.
    /// </summary>
    private static HashSet<string> OutputLineSet(string? output)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output))
            return set;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }
        return set;
    }

    /// <summary>
    /// Integrates the guard check into the stage-9 gate. Runs the guard,
    /// returns whether the stage should be red, the guard output for
    /// fix-verify, and whether the guard timed out. Pre-existing-only guard
    /// violations add a ledger note but don't block the commit.
    /// </summary>
    private async Task<(bool GuardFailed, string? GuardOutput, bool TimedOut)> IntegrateGuardAsync(
        string rootPath,
        string taskId,
        string runId,
        RelayConfig config,
        StringBuilder ledger,
        CancellationToken ct)
    {
        if (config.GuardCommand is null)
            return (false, null, false);

        var (newViolations, fullOutput, timedOut) = await RunGuardCheckAsync(
            rootPath, taskId, runId, _dependencies.TestRunner,
            config.FormatCommand, config.GuardCommand, config.BaselineVerify, ct);

        if (timedOut)
            return (false, newViolations, true);

        if (newViolations is not null)
            return (true, newViolations, false);

        // Guard exited non-zero but all violations are pre-existing.
        if (!string.IsNullOrWhiteSpace(fullOutput))
        {
            ledger.AppendLine("> **Note**: pre-existing guard violations detected (not caused by this task).");
            ledger.AppendLine();
        }

        return (false, null, false);
    }

    /// <summary>
    /// Builds the combined failure output string for the fix-verify loop
    /// from test, guard, and bootstrap failures.
    /// </summary>
    private static string BuildFailureOutput(
        TestRunResult testResult,
        string? guardOutput,
        bool bootstrapFailed,
        string? bootstrapFailureOutput)
    {
        var parts = new List<string>();
        if (testResult.ExitCode != 0)
            parts.Add(testResult.Output);
        if (guardOutput is not null)
            parts.Add("--- Guard check output ---\n" + guardOutput);
        if (bootstrapFailed && bootstrapFailureOutput is not null)
        {
            if (testResult.ExitCode != 0)
                parts.Add("--- Bootstrap check output ---\n" + bootstrapFailureOutput);
            else
                parts.Add("Bootstrap check failed:\n" + bootstrapFailureOutput);
        }
        return string.Join("\n\n", parts);
    }
}
