using System.Text;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private static string? ReadOptionalString(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task WritePreRunUntrackedAsync(string path, IReadOnlySet<string> paths, CancellationToken ct)
    {
        var sorted = paths.Order(StringComparer.Ordinal);
        await File.WriteAllTextAsync(
            path,
            string.Join(Environment.NewLine, sorted) + Environment.NewLine,
            ct);
    }

    private static async Task<IReadOnlySet<string>> ReadPreRunUntrackedAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.Ordinal);

        var lines = await File.ReadAllLinesAsync(path, ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }

        return set;
    }

    /// <summary>
    /// Captures a pre-run untracked snapshot. On resume reuses the persisted
    /// first-instance snapshot; on fresh runs captures current state.
    /// When <paramref name="forceFresh"/> is true, always captures a new
    /// snapshot even on resume (used when a re-added task starts fresh).
    /// </summary>
    private async Task<IReadOnlySet<string>?> CapturePreRunUntrackedAsync(
        string rootPath,
        string taskDirectory,
        bool forceFresh = false,
        CancellationToken cancellationToken = default)
    {
        IReadOnlySet<string>? preRunUntracked = null;
        if (_options.CreateGitCommit)
        {
            var snapshotPath = Path.Combine(taskDirectory, "pre-run-untracked.txt");
            if (_options.Resume && !forceFresh && File.Exists(snapshotPath))
            {
                preRunUntracked = await ReadPreRunUntrackedAsync(snapshotPath, cancellationToken);
            }
            else if (_options.Resume && !forceFresh)
            {
                preRunUntracked = new HashSet<string>(StringComparer.Ordinal);
            }
            else
            {
                preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(rootPath, cancellationToken, _dependencies.GitInvoker);
                await WritePreRunUntrackedAsync(snapshotPath, preRunUntracked, cancellationToken);
            }
        }
        return preRunUntracked;
    }

    /// <summary>
    /// Records the run-base sha — the HEAD commit when the task began — so the
    /// Commit stage can squash any commits the agent made itself during the run
    /// into the single sealed commit (parented on the run-base). Persisted to the
    /// task directory so it survives a resume. On resume reuses the persisted
    /// value; a re-added task (or missing snapshot) captures the current HEAD.
    /// Returns <c>null</c> when commits are disabled or HEAD cannot be resolved
    /// (e.g. an empty repo with no commit yet), in which case the squash is
    /// skipped and the existing single-commit behaviour is unchanged.
    /// </summary>
    private async Task<string?> CaptureRunBaseShaAsync(
        string rootPath,
        string taskDirectory,
        bool forceFresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CreateGitCommit)
            return null;

        var runBasePath = Path.Combine(taskDirectory, "run-base.txt");
        if (_options.Resume && !forceFresh && File.Exists(runBasePath))
        {
            var saved = (await File.ReadAllTextAsync(runBasePath, cancellationToken)).Trim();
            return string.IsNullOrEmpty(saved) ? null : saved;
        }

        var head = await _dependencies.GitInvoker.RunAsync(
            rootPath, ["rev-parse", "HEAD"], cancellationToken);
        if (head.ExitCode != 0)
            return null;
        var sha = head.Output.Trim();
        if (string.IsNullOrEmpty(sha))
            return null;

        await File.WriteAllTextAsync(runBasePath, sha + Environment.NewLine, cancellationToken);
        return sha;
    }

    /// <summary>
    /// Plan-completeness gate: on coverage gap, issues one corrective retry
    /// of stage 4 with the gap error in <c>LastTestOutput</c>.  Returns the
    /// (possibly updated) stage-4 JSON body, updated targeted test command,
    /// and cost-tracking deltas.
    /// </summary>
    private async Task<(string Body, string TargetedTestCommand, double CostDelta, int UnknownDelta)>
        TryPlanCompletenessRetryAsync(
            string body, JsonElement json, List<string> manifest,
            string rootPath, string runId, string taskId, string taskDirectory,
            RelayConfig config, RelayStageDefinition stage, RelayTaskInput input,
            StringBuilder ledger, string? pinnedSwivalProfileContent,
            string targetedTestCommand,
            CancellationToken cancellationToken)
    {
        if (_options.LastStageToRun == 4) return (body, targetedTestCommand, 0, 0);
        var pn = ReadOptionalString(json, "plan");
        if (pn is null) return (body, targetedTestCommand, 0, 0);
        var ce = PlanCompletenessGate.CheckCoverage(pn, manifest, input.Markdown);
        if (ce is null) return (body, targetedTestCommand, 0, 0);

        var ri = BuildInvocation(rootPath, runId, taskId, taskDirectory,
            config, stage, input, ledger, manifest,
            pinnedSwivalProfileContent: pinnedSwivalProfileContent);
        var rr = await _dependencies.SubagentRunner.RunAsync(
            ri with { LastTestOutput = ce }, cancellationToken);
        double cd = 0; int ud = 0;
        if (TryEstimateCost(ri.ReportFile) is { } rc) cd = rc.CostUsd; else ud = 1;

        if (rr.IsValid && !string.IsNullOrWhiteSpace(rr.Json)
            && TryParseContractJson(rr.Json, out var rj, out _))
        {
            manifest.Clear();
            manifest.AddRange(ReadStringArray(rj, "manifest")
                .Distinct(StringComparer.Ordinal)
                .Where(e => !IsPathUnderDirectory(rootPath, e, config.TasksDir))
                .Select(e => e.StartsWith('+') ? e[1..] : e));
            var ttc = BuildTargetedTestCommand(config, manifest);
            await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
            return (rr.Json, ttc, cd, ud);
        }
        return (body, targetedTestCommand, cd, ud);
    }
}
