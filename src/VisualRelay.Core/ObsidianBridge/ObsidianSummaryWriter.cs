using System.Text;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.ObsidianBridge;

/// <summary>
/// Builds and writes human-readable run summaries into the Obsidian vault's
/// <c>Completed/</c> folder. Summaries are pure functions of on-disk artifacts:
/// <see cref="RelayRunHistory.ReadTaskMetric"/> and
/// <see cref="RelayRunHistory.ReadStatusRecord"/>.
/// </summary>
public sealed class ObsidianSummaryWriter
{
    /// <summary>
    /// Builds the summary markdown for a completed task.
    /// </summary>
    public string Build(
        string rootPath,
        string taskId,
        RelayTaskOutcome? outcome,
        string specMarkdown,
        Guid? sourceGuid,
        DateTimeOffset nowUtc)
    {
        var metric = RelayRunHistory.ReadTaskMetric(rootPath, taskId);
        var statusEntries = RelayRunHistory.ReadStatusRecord(rootPath, taskId);
        var repoName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var status = ResolveStatus(outcome, statusEntries);
        var commitSha = outcome?.CommitSha ?? "";
        var costUsd = metric.CostUsd;
        var duration = metric.DurationSeconds;
        var durationLabel = FormatDuration(duration);
        var completedAt = ResolveCompletionDate(metric, rootPath, taskId, nowUtc);
        var reason = outcome?.Reason;

        var sb = new StringBuilder();

        // Frontmatter.
        sb.AppendLine("---");
        sb.AppendLine($"vr-task-id: {taskId}");
        sb.AppendLine($"vr-status: {status}");
        sb.AppendLine($"vr-repo: {repoName}");
        sb.AppendLine($"vr-completed-at: {completedAt:yyyy-MM-ddTHH:mm:sszzz}");
        sb.AppendLine($"vr-commit: {commitSha}");
        sb.AppendLine($"vr-cost-usd: {MoneyFormatter.Dollars(costUsd)}");
        sb.AppendLine($"vr-duration: {durationLabel}");
        sb.Append("vr-source-guid: ");
        if (sourceGuid.HasValue)
            sb.AppendLine(sourceGuid.Value.ToString());
        else
            sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Title.
        sb.AppendLine($"# {taskId}");
        sb.AppendLine();

        // Status line.
        var statusDisplay = StatusToDisplay(status);
        sb.Append($"**Status:** {statusDisplay}");
        if (!string.IsNullOrWhiteSpace(commitSha))
            sb.Append($" · **Commit:** `{commitSha}`");
        sb.Append($" · **Cost:** {MoneyFormatter.Dollars(costUsd)}");
        sb.Append($" · **Duration:** {durationLabel}");
        sb.Append($" · **Completed:** {completedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine();

        // One-line outcome.
        if (!string.IsNullOrWhiteSpace(reason))
        {
            sb.AppendLine($"> {reason}");
            sb.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(commitSha))
        {
            sb.AppendLine($"> Committed `{commitSha}`.");
            sb.AppendLine();
        }

        // Stages table.
        sb.AppendLine("## Stages");
        sb.AppendLine();
        if (statusEntries.Count > 0)
        {
            sb.AppendLine("| # | Stage | Status | Check | Model | Turns | Time | Cost |");
            sb.AppendLine("|---|-------|--------|-------|-------|-------|------|------|");

            foreach (var entry in statusEntries.OrderBy(e => e.Stage))
            {
                var stageMetric = metric.Stages.FirstOrDefault(s => s.StageNumber == entry.Stage);
                var check = entry.Check ?? "–";
                var model = stageMetric?.Model ?? entry.Model ?? "";
                var turns = stageMetric?.Turns.ToString() ?? "";
                var time = stageMetric?.DurationLabel ?? "";
                var cost = stageMetric?.CostLabel ?? "";

                sb.Append($"| {entry.Stage} | {entry.Name} | {entry.Status}");
                sb.Append($" | {check}");
                sb.Append($" | {model}");
                sb.Append($" | {turns}");
                sb.Append($" | {time}");
                sb.Append($" | {cost}");
                sb.AppendLine(" |");
            }
        }
        else
        {
            // Fallback: build from metrics only.
            sb.AppendLine("| # | Stage | Status | Check | Model | Turns | Time | Cost |");
            sb.AppendLine("|---|-------|--------|-------|-------|-------|------|------|");

            foreach (var stage in metric.Stages.OrderBy(s => s.StageNumber))
            {
                sb.Append($"| {stage.StageNumber} | {stage.StageName}");
                sb.Append(" | Done");
                sb.Append(" | –");
                sb.Append($" | {stage.Model}");
                sb.Append($" | {stage.Turns}");
                sb.Append($" | {stage.DurationLabel}");
                sb.Append($" | {stage.CostLabel}");
                sb.AppendLine(" |");
            }
        }

        sb.AppendLine();

        // Task spec embedded.
        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.Append(specMarkdown);

        return sb.ToString();
    }

    /// <summary>
    /// Writes the summary markdown to the vault's <c>Completed/&lt;date&gt;/&lt;id&gt;.md</c> path.
    /// Creates directories as needed. Overwrites any existing summary.
    /// </summary>
    public void Write(
        ObsidianVaultLayout layout,
        string rootPath,
        string taskId,
        RelayTaskOutcome? outcome,
        string specMarkdown,
        Guid? sourceGuid,
        DateTimeOffset nowUtc)
    {
        var metric = RelayRunHistory.ReadTaskMetric(rootPath, taskId);
        var completedDate = ResolveCompletionDateOnly(metric, rootPath, taskId, nowUtc);
        var summaryPath = layout.SummaryPath(taskId, completedDate);

        var markdown = Build(rootPath, taskId, outcome, specMarkdown, sourceGuid, nowUtc);

        var dir = Path.GetDirectoryName(summaryPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(summaryPath, markdown);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string ResolveStatus(
        RelayTaskOutcome? outcome,
        IReadOnlyList<StageStatusEntry> statusEntries)
    {
        if (outcome is not null)
        {
            return outcome.Status switch
            {
                RelayTaskOutcomeStatus.Committed => "committed",
                RelayTaskOutcomeStatus.Flagged => "needs-review",
                RelayTaskOutcomeStatus.Failed => "failed",
                _ => "committed"
            };
        }

        // Infer from status record.
        if (statusEntries.Any(e =>
                string.Equals(e.Status, "Flagged", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Status, "Failed", StringComparison.OrdinalIgnoreCase)))
        {
            // Check if any are flagged (needs-review) vs failed.
            if (statusEntries.Any(e => string.Equals(e.Status, "Flagged", StringComparison.OrdinalIgnoreCase)))
                return "needs-review";
            if (statusEntries.Any(e => string.Equals(e.Status, "Failed", StringComparison.OrdinalIgnoreCase)))
                return "failed";
        }

        return "committed";
    }

    private static string StatusToDisplay(string status) => status switch
    {
        "committed" => "Committed",
        "needs-review" => "Needs review",
        "failed" => "Failed",
        _ => "Committed"
    };

    private static DateTimeOffset ResolveCompletionDate(
        TaskRunMetric metric,
        string rootPath,
        string taskId,
        DateTimeOffset fallback)
    {
        // Tier 1: max stage Timestamp.
        if (metric.Stages.Count > 0)
        {
            var maxTimestamp = metric.Stages.Max(s => s.Timestamp);
            if (maxTimestamp != default)
                return maxTimestamp;
        }

        // Tier 2: newest file mtime in .relay/<taskId>/.
        var relayDir = Path.Combine(rootPath, ".relay", taskId);
        if (Directory.Exists(relayDir))
        {
            var files = Directory.EnumerateFiles(relayDir);
            DateTimeOffset? newestMtime = null;
            foreach (var file in files)
            {
                var mtime = File.GetLastWriteTimeUtc(file);
                if (newestMtime is null || mtime > newestMtime.Value)
                    newestMtime = mtime;
            }

            if (newestMtime.HasValue)
                return newestMtime.Value;
        }

        // Tier 3: fallback to nowUtc.
        return fallback;
    }

    private static DateOnly ResolveCompletionDateOnly(
        TaskRunMetric metric,
        string rootPath,
        string taskId,
        DateTimeOffset fallback)
    {
        var dt = ResolveCompletionDate(metric, rootPath, taskId, fallback);
        return DateOnly.FromDateTime(dt.Date);
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
            return $"{Math.Max(0, seconds):0}s";

        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }
}
