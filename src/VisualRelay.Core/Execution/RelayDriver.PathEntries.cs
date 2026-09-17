using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Resolves a model-written path list through <see cref="ManifestPaths"/> and
    /// reports whatever it refused. Every reader of such a list goes through here:
    /// the plan's manifest, the plan-completeness retry's manifest and Author-tests'
    /// declared test files. A refusal used to be a silent mangling — the file simply
    /// stopped existing for the commit, the red gate and the test count — so it now
    /// leaves a ledger note beside the task-dir one and a warn event per entry.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolvePathEntriesAsync(
        string rootPath,
        string runId,
        string taskId,
        int stageNumber,
        string listName,
        IEnumerable<string> entries,
        StringBuilder? ledger,
        CancellationToken cancellationToken)
    {
        var (resolved, dropped) = ManifestPaths.ResolveAll(rootPath, entries);
        if (dropped.Count == 0)
            return resolved;

        var reasons = string.Join("; ", dropped.Select(drop => drop.Reason).Distinct(StringComparer.Ordinal));
        var names = string.Join(", ", dropped.Select(drop => $"`{drop.Entry}`"));
        ledger?.AppendLine(dropped.Count == 1
            ? $"> **Note**: dropped 1 entry from {listName} ({reasons}): {names}"
            : $"> **Note**: dropped {dropped.Count} entries from {listName} ({reasons}): {names}");
        ledger?.AppendLine();

        foreach (var drop in dropped)
        {
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "warn", "path_entry_dropped", runId, rootPath, taskId, stageNumber,
                Data: new Dictionary<string, string>
                {
                    ["list"] = listName,
                    ["entry"] = drop.Entry,
                    ["reason"] = drop.Reason,
                }), cancellationToken);
        }

        return resolved;
    }

    /// <summary>
    /// The run's opening event. No base_url: there is no single endpoint any more —
    /// each tier resolves its own provider and the served model is recorded per stage
    /// on the report. <c>resume</c> and <c>firstStage</c> are here because a resumed
    /// run's log did not say it was one, so a run that silently restarted at stage 5
    /// read as a fresh run that had skipped four stages.
    /// </summary>
    private async Task PublishRunStartAsync(
        string rootPath, string runId, string taskId, int firstStageToRun, bool isReAdded,
        CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>
        {
            ["version"] = VersionHelper.ReadInformationalVersion(),
            ["resume"] = _options.Resume ? "true" : "false",
            ["firstStage"] = firstStageToRun.ToString(),
        };
        if (isReAdded)
            data["fresh"] = "prior state archived (re-added task)";

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "run_start", runId, rootPath, taskId, Data: data), cancellationToken);
    }
}
