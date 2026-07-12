using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

/// <summary>
/// The relevant failure context read from a flagged run's <c>.relay/&lt;taskId&gt;/</c>
/// directory — flag reason, verified failures, and ledger summaries — so a
/// follow-up fix task can be authored with the right signal.
/// </summary>
public sealed record FailedRunContext(
    string? FlagReason,
    int? FlaggedStage,
    string? FlaggedStageName,
    string? FlaggedStageError,
    IReadOnlyList<FailedVerifyOutput> VerifyOutputs,
    string? LedgerSummary);

/// <summary>
/// A digested summary of one verify-output file — its stage/attempt numbers
/// and the extracted <c>[FAIL]</c> lines plus the final pass/fail tally.
/// </summary>
public sealed record FailedVerifyOutput(int Stage, int Attempt, string Summary);

/// <summary>
/// Reads <c>.relay/&lt;taskId&gt;/</c> on disk and returns a
/// <see cref="FailedRunContext"/>. Never throws — missing or unreadable files
/// produce null/empty fields.
/// </summary>
public static class FailedRunContextReader
{
    public static FailedRunContext Read(string taskDirectory)
    {
        // ── NEEDS-REVIEW ────────────────────────────────────────────────
        string? flagReason = null;
        int? flaggedStage = null;
        var needsReviewPath = Path.Combine(taskDirectory, "NEEDS-REVIEW");
        if (File.Exists(needsReviewPath))
        {
            try
            {
                var lines = File.ReadAllLines(needsReviewPath);
                if (lines.Length > 0)
                    flagReason = lines[0].Trim();

                foreach (var line in lines)
                {
                    var m = Regex.Match(line, @"stage\s+(\d+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        flaggedStage = int.Parse(m.Groups[1].Value);
                        break;
                    }
                }
            }
            catch
            {
                // Best-effort — leave nulls.
            }
        }

        // ── status.json → flagged stage entry ──────────────────────────
        string? flaggedStageName = null;
        string? flaggedStageError = null;
        try
        {
            var entries = StageStatusRecord.Read(taskDirectory);
            var flagged = entries.FirstOrDefault(e => e.Status == "Flagged");
            if (flagged is not null)
            {
                flaggedStageName = flagged.Name;
                flaggedStageError = flagged.Error;
            }
        }
        catch
        {
            // Best-effort.
        }

        // ── verify-output files ────────────────────────────────────────
        var verifyOutputs = new List<FailedVerifyOutput>();
        if (Directory.Exists(taskDirectory))
        {
            foreach (var file in Directory.GetFiles(taskDirectory, "stage*-attempt*.verify-output.txt"))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file); // e.g. stage9-attempt1.verify-output
                    var m = Regex.Match(fileName, @"stage(\d+)-attempt(\d+)");
                    if (!m.Success)
                        continue;

                    var stage = int.Parse(m.Groups[1].Value);
                    var attempt = int.Parse(m.Groups[2].Value);
                    var tail = ReadTail(file, 200);
                    var summary = ExtractSummary(tail);

                    // Try to enrich with non-test check evidence from sibling .verify-checks.json.
                    var checksPath = file.Replace(".verify-output.txt", ".verify-checks.json", StringComparison.Ordinal);
                    var checksSummary = ReadChecksSummary(checksPath);
                    if (checksSummary.Length > 0)
                    {
                        summary = string.IsNullOrEmpty(summary)
                            ? checksSummary
                            : checksSummary + "\n" + summary;
                    }

                    // If still empty, fall back to the raw verify-output tail.
                    if (string.IsNullOrEmpty(summary))
                    {
                        summary = TruncateToTail(tail, 40, 4_000);
                    }

                    verifyOutputs.Add(new FailedVerifyOutput(stage, attempt, summary));
                }
                catch
                {
                    // Skip unparseable files.
                }
            }
        }

        // ── ledger.md summary ──────────────────────────────────────────
        string? ledgerSummary = null;
        var ledgerPath = Path.Combine(taskDirectory, "ledger.md");
        if (File.Exists(ledgerPath))
        {
            try
            {
                var content = File.ReadAllText(ledgerPath);
                ledgerSummary = content.Length > 2000 ? content[..2000] : content;
            }
            catch
            {
                // Best-effort.
            }
        }

        return new FailedRunContext(
            flagReason, flaggedStage, flaggedStageName, flaggedStageError,
            verifyOutputs, ledgerSummary);
    }

    private static string ReadChecksSummary(string checksPath)
    {
        if (!File.Exists(checksPath))
            return string.Empty;

        try
        {
            var json = File.ReadAllText(checksPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new StringBuilder();
            AppendCheckSection(sb, "bootstrap check", "bootstrapCheck", "bootstrapOutput", root);
            AppendCheckSection(sb, "guard check", "guardCheck", "guardOutput", root);
            AppendCheckSection(sb, "new-guard-probe check", "newGuardProbeCheck", "newGuardProbeOutput", root);

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AppendCheckSection(
        StringBuilder sb,
        string label,
        string checkProp,
        string outputProp,
        JsonElement root)
    {
        if (!root.TryGetProperty(checkProp, out var checkEl) || checkEl.GetString() != "red")
            return;

        sb.AppendLine($"{label}: red");

        if (root.TryGetProperty(outputProp, out var outputEl))
        {
            var output = outputEl.GetString();
            if (!string.IsNullOrWhiteSpace(output))
            {
                sb.Append(TruncateToTail(output, 40, 4_000));
                if (!output.EndsWith('\n'))
                    sb.AppendLine();
            }
        }
    }

    private static string TruncateToTail(string content, int maxLines, int maxChars)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > maxLines)
            lines = lines[^maxLines..];

        var joined = string.Join('\n', lines);
        if (joined.Length > maxChars)
            joined = joined[^maxChars..];

        return joined;
    }

    private static string ReadTail(string filePath, int maxLines)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length <= maxLines)
                return string.Join('\n', lines);

            return string.Join('\n', lines[^maxLines..]);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractSummary(string content)
    {
        var lines = content.Split('\n');
        var failLines = lines.Where(l => l.Contains("[FAIL]")).Take(20).ToList();
        var summaryLine = lines.LastOrDefault(
            l => l.Contains("Failed:") || l.Contains("Passed:"))?.Trim();

        var parts = new List<string>();
        parts.AddRange(failLines);
        if (summaryLine is not null)
            parts.Add(summaryLine);

        return string.Join('\n', parts);
    }
}
