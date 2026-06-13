using System.Text.Json;

namespace VisualRelay.Domain;

/// <summary>
/// A single entry in the per-stage status record.
/// Written by the driver at each lifecycle point and read by the UI as the
/// single source of truth for stage status. This is a committed proof file
/// (force-added alongside ledger.md / *.seals / manifest.txt), not local run state.
/// </summary>
public sealed record StageStatusEntry(
    int Stage,
    string Name,
    string Status,
    string? Check = null,
    double? DurationSeconds = null,
    double? CostUsd = null,
    int? Turns = null,
    string? Model = null,
    string? Error = null,
    string? TaskInputHash = null);

/// <summary>
/// Serializer / deserializer for the per-stage status record.
/// </summary>
public static class StageStatusRecord
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Atomically writes the status record to disk.
    /// </summary>
    public static async Task WriteAsync(string taskDirectory, IReadOnlyList<StageStatusEntry> entries, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(taskDirectory, "status.json");
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(
            tmp,
            JsonSerializer.Serialize(entries, Options),
            cancellationToken);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the status record from disk. Returns empty list when the file is missing
    /// or unreadable.
    /// </summary>
    public static IReadOnlyList<StageStatusEntry> Read(string taskDirectory)
    {
        var path = Path.Combine(taskDirectory, "status.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IReadOnlyList<StageStatusEntry>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
