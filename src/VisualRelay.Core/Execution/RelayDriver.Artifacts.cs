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
        {
            return $"{Math.Max(0, seconds):0}s";
        }

        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }
}
