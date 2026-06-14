using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Traces;
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
    /// Parses contract JSON defensively: validates the root is a JSON object and
    /// returns a descriptive error on any failure. Callers never throw on malformed
    /// or wrong-shaped contract output — they flag cleanly with the returned message.
    /// </summary>
    internal static bool TryParseContractJson(string? json, out JsonElement element, out string? error)
    {
        error = null;
        element = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "contract JSON is null or empty";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"contract root must be a JSON object, but got {doc.RootElement.ValueKind}";
                return false;
            }

            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid contract JSON: {ex.Message}";
            return false;
        }
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
    { ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".csv" };

    /// <summary>
    /// Returns true when <paramref name="path"/> is implementation code —
    /// anything with an extension not in the non-code allowlist.
    /// Files with no extension are treated as non-code (docs/config/data).
    /// Unknown extensions default to code (fail-safe toward requiring a test).
    /// </summary>
    private static bool IsImpl(string path) =>
        Path.GetExtension(path) is { Length: > 0 } ext && !NonCodeExtensions.Contains(ext);

    /// <summary>
    /// Returns <paramref name="config"/>.TestFileCommand with the <c>{files}</c> token replaced
    /// by the space-joined non-code files from <paramref name="manifest"/> (i.e. the complement
    /// of <see cref="IsImpl"/>). Falls back to <paramref name="config"/>.TestCommand when
    /// <c>testFileCmd</c> contains no <c>{files}</c> token, or when the manifest has no
    /// non-code files after filtering.
    /// </summary>
    internal static string BuildTargetedTestCommand(RelayConfig config, IReadOnlyList<string> manifest)
    {
        if (!config.TestFileCommand.Contains("{files}", StringComparison.Ordinal))
            return config.TestCommand;
        var testFiles = manifest.Where(f => !IsImpl(f)).ToList();
        if (testFiles.Count == 0)
            return config.TestCommand;
        return config.TestFileCommand.Replace("{files}", string.Join(' ', testFiles),
            StringComparison.Ordinal);
    }

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

    // ── Bootstrap detection ────────────────────────────────────────────

    private static readonly IReadOnlyList<string> BuiltInBootstrapGlobs =
    [
        "flake.nix", "flake.lock", "*.nix", "Brewfile", "Dockerfile*",
        ".tool-versions", "rust-toolchain*"
    ];

    private const string BuiltInBootstrapCommand = "nix develop --command true";

    /// <summary>
    /// Simple filename-based glob matching. A glob starting with <c>*</c> matches
    /// any filename whose suffix matches the rest; a glob ending with <c>*</c>
    /// matches any filename whose prefix matches the rest; otherwise exact match.
    /// </summary>
    private static bool MatchesBootstrapGlob(string relativePath, string glob)
    {
        var fileName = Path.GetFileName(relativePath);
        if (glob.StartsWith('*'))
            return fileName.EndsWith(glob[1..], StringComparison.Ordinal);
        if (glob.EndsWith('*'))
            return fileName.StartsWith(glob[..^1], StringComparison.Ordinal);
        return string.Equals(fileName, glob, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <c>(shouldRun: true, command)</c> when the manifest touches a
    /// bootstrap file and a smoke command is resolved; otherwise
    /// <c>(false, "")</c>.
    /// </summary>
    private static (bool ShouldRun, string Command) ResolveBootstrapCheck(
        RelayConfig config,
        IReadOnlyList<string> manifest)
    {
        var globs = config.BootstrapFiles is { Count: > 0 }
            ? config.BootstrapFiles
            : BuiltInBootstrapGlobs;

        // An explicit empty-string entry disables built-in detection.
        if (globs is [""])
            return (false, string.Empty);

        var matchedAny = manifest.Any(path =>
            globs.Any(glob => MatchesBootstrapGlob(path, glob)));

        if (!matchedAny)
            return (false, string.Empty);

        string? command = config.BootstrapCheckCommand;
        if (command is not null)
            return (true, command);

        // Auto-detect: only nix repos (any .nix file matched) get the built-in.
        var hasNix = manifest.Any(path =>
            Path.GetExtension(path).Equals(".nix", StringComparison.OrdinalIgnoreCase));
        if (hasNix)
            return (true, BuiltInBootstrapCommand);

        // Matched a glob but no recognised toolchain — skip.
        return (false, string.Empty);
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

    private static string FormatDuration(double seconds) =>
        seconds < 60 ? $"{Math.Max(0, seconds):0}s" : $"{Math.Floor(seconds / 60):0}m {seconds % 60:00}s";

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

}
