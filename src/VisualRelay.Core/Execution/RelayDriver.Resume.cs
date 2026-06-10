using System.Text;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Loads prior-run state for a resume: ledger, seals, manifest, costs, status entries,
    /// and determines <paramref name="firstStageToRun"/> from the authoritative status record.
    /// When no prior <c>status.json</c> exists this is a no-op (fresh run).
    /// </summary>
    private static void LoadResumeState(
        string taskDirectory, string taskId, StringBuilder ledger, List<string> manifest,
        List<string> seals, ref string previousSeal, ref string taskHash,
        ref double sessionCostUsd, ref int unknownCostStageCount,
        List<StageStatusEntry> statusEntries, ref int firstStageToRun)
    {
        var priorStatus = StageStatusRecord.Read(taskDirectory);
        if (priorStatus.Count == 0)
            return;
        var firstNonDone = priorStatus.FirstOrDefault(e => e.Status != "Done");
        firstStageToRun = firstNonDone?.Stage ?? (RelayStages.All.Count + 1);

        // Load ledger, seals (extracting last seal hash), and manifest.
        var ledgerPath = Path.Combine(taskDirectory, "ledger.md");
        if (File.Exists(ledgerPath))
            ledger.Append(File.ReadAllText(ledgerPath));
        var sealsPath = Path.Combine(taskDirectory, $"{taskId}.seals");
        if (File.Exists(sealsPath))
        {
            foreach (var line in File.ReadAllLines(sealsPath))
                if (!string.IsNullOrWhiteSpace(line)) seals.Add(line);
            if (seals.Count > 0)
            {
                using var doc = JsonDocument.Parse(seals[^1]);
                if (doc.RootElement.TryGetProperty("seal", out var sp))
                    taskHash = previousSeal = sp.GetString() ?? string.Empty;
            }
        }
        var manifestPath = Path.Combine(taskDirectory, "manifest.txt");
        if (File.Exists(manifestPath))
            manifest.AddRange(File.ReadAllLines(manifestPath).Where(l => !string.IsNullOrWhiteSpace(l)));

        // Accumulate costs from prior Done stages before the resume point.
        foreach (var entry in priorStatus)
        {
            if (entry.Status == "Done" && entry.Stage < firstStageToRun)
            {
                sessionCostUsd += entry.CostUsd ?? 0;
                if (entry.CostUsd == null && entry.Stage != 11)
                    unknownCostStageCount++;
            }
        }

        // Clone prior status; reset Done entries at/after resume point to Waiting.
        statusEntries.Clear();
        foreach (var entry in priorStatus)
            statusEntries.Add(entry.Status == "Done" && entry.Stage >= firstStageToRun
                ? entry with { Status = "Waiting" } : entry);
        while (statusEntries.Count < RelayStages.All.Count)
        {
            var n = statusEntries.Count + 1;
            statusEntries.Add(new StageStatusEntry(n, RelayStages.All[n - 1].Name, "Waiting"));
        }
    }
}
