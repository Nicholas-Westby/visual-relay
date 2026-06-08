using System.Text;
using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private static async Task WriteManifestAsync(string taskDirectory, IReadOnlyList<string> manifest, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "manifest.txt"),
            string.Join(Environment.NewLine, manifest) + Environment.NewLine,
            cancellationToken);
    }

    private static async Task WriteArtifactsAsync(
        string taskDirectory,
        string taskId,
        string ledger,
        IReadOnlyList<string> seals,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "ledger.md"), ledger, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, $"{taskId}.seals"),
            string.Join(Environment.NewLine, seals) + Environment.NewLine,
            cancellationToken);
    }

    private static void AppendLedgerSection(StringBuilder ledger, RelayStageDefinition stage, string body)
    {
        ledger.AppendLine($"## Stage {stage.Number} - {stage.Name}");
        ledger.AppendLine();
        ledger.AppendLine(body);
        ledger.AppendLine();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement json, string propertyName)
    {
        if (!json.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Sanitizes each raw candidate via <see cref="CommitMessageSanitizer.TrySanitizeSubject"/>,
    /// drops non-Conventional entries, and appends the guaranteed <c>chore(relay): {taskId}</c>
    /// fallback so the list is never empty.
    /// </summary>
    private static IReadOnlyList<string> BuildCommitChain(IReadOnlyList<string> rawCandidates, string taskId)
    {
        var chain = new List<string>(rawCandidates.Count + 1);
        foreach (var raw in rawCandidates)
        {
            var subject = CommitMessageSanitizer.TrySanitizeSubject(raw);
            if (subject is not null)
            {
                chain.Add(subject);
            }
        }

        chain.Add(CommitMessageSanitizer.FromRawOrFallback(null, taskId));
        return chain;
    }

    private static readonly HashSet<string> NonCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".csv"
    };

    /// <summary>
    /// Returns true when <paramref name="path"/> is implementation code —
    /// anything with an extension not in the non-code allowlist.
    /// Files with no extension are treated as non-code (docs/config/data).
    /// Unknown extensions default to code (fail-safe toward requiring a test).
    /// </summary>
    private static bool IsImpl(string path) =>
        Path.GetExtension(path) is { Length: > 0 } ext && !NonCodeExtensions.Contains(ext);

    private static string? ReadOptionalString(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string WorkingTreeHash(string rootPath, IReadOnlyList<string> manifest)
    {
        var parts = new List<string>();
        foreach (var relative in manifest.Order(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(rootPath, relative);
            parts.Add(relative);
            parts.Add(File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty);
        }

        return Hashing.Sha256Hex(parts.ToArray());
    }

    private static bool IsPathUnderDirectory(string rootPath, string relativePath, string directoryName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var dirFullPath = Path.GetFullPath(Path.Combine(rootPath, directoryName));
        return fullPath.StartsWith(dirFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, dirFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string SerializeSeal(int stageNumber, string artifactHash, string treeHash, string seal, string? check)
    {
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = "stage",
            ["n"] = stageNumber,
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["artifactHash"] = artifactHash,
            ["treeHash"] = treeHash,
            ["seal"] = seal
        };
        if (check is not null)
        {
            payload["check"] = check;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static RelayCostEstimate? TryEstimateCost(string reportFile)
    {
        if (!File.Exists(reportFile))
        {
            return null;
        }

        try
        {
            return RelayCostEstimator.EstimateReport(reportFile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
            return $"{Math.Max(0, seconds):0}s";
        var minutes = Math.Floor(seconds / 60);
        return $"{minutes:0}m {seconds % 60:00}s";
    }

    // -- Status record helpers --

    private static List<StageStatusEntry> SeedStatusEntries()
    {
        var entries = new List<StageStatusEntry>(RelayStages.All.Count);
        foreach (var stage in RelayStages.All)
        {
            entries.Add(new StageStatusEntry(stage.Number, stage.Name, "Waiting"));
        }
        return entries;
    }

    private static void MarkStatus(List<StageStatusEntry> entries, int stageNumber, string status)
    {
        var idx = stageNumber - 1;
        if (idx >= 0 && idx < entries.Count)
        {
            entries[idx] = entries[idx] with { Status = status };
        }
    }

    private static void MarkStatusDone(List<StageStatusEntry> entries, RelayStageDefinition stage, TimeSpan elapsed, RelayCostEstimate? cost, string? check)
    {
        var idx = stage.Number - 1;
        if (idx < 0 || idx >= entries.Count) return;
        entries[idx] = entries[idx] with
        {
            Status = "Done",
            DurationSeconds = cost?.DurationSeconds > 0 ? cost.DurationSeconds : elapsed.TotalSeconds,
            CostUsd = stage.Kind == "driver" ? 0 : cost?.CostUsd,
            Turns = cost?.Turns > 0 ? cost.Turns : null,
            Model = stage.Kind == "driver" ? null : cost?.Model,
            Check = check
        };
    }

    private static void MarkStatusFlagged(List<StageStatusEntry> entries, int stageNumber, string error)
    {
        var idx = stageNumber - 1;
        if (idx < 0 || idx >= entries.Count) return;
        entries[idx] = entries[idx] with { Status = "Flagged", Error = error };
    }

    private static int FindRunningStage(IReadOnlyList<StageStatusEntry> entries)
    {
        return entries.FirstOrDefault(e => e.Status == "Running")?.Stage ?? 1;
    }

    private static async Task WriteStatusAsync(string taskDirectory, List<StageStatusEntry> entries, CancellationToken cancellationToken)
    {
        await StageStatusRecord.WriteAsync(taskDirectory, entries, cancellationToken);
    }

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
