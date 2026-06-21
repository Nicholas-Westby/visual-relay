using System.Text.Json;
using VisualRelay.Core.Traces;

namespace VisualRelay.Core.Execution;

public sealed record StageInputArtifact(
    int Version,
    int Stage,
    int Attempt,
    string Name,
    string SystemPrompt,
    string InputPrompt,
    string Timestamp)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Derive the sibling <c>.input.json</c> path from a
    /// <c>stage{N}-attempt{M}.report.json</c> path by swapping the extension.
    /// </summary>
    public static string PathFor(string reportFilePath)
    {
        ArgumentNullException.ThrowIfNull(reportFilePath);
        var dir = Path.GetDirectoryName(reportFilePath)!;
        var name = Path.GetFileName(reportFilePath);
        var inputName = name.Replace(".report.json", ".input.json");
        return Path.Combine(dir, inputName);
    }

    /// <summary>Serialize <paramref name="data"/> to <see cref="PathFor"/>.</summary>
    public static void Write(string reportFilePath, StageInputArtifact data)
    {
        var path = PathFor(reportFilePath);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Tolerant read — returns <c>false</c> on missing or corrupt files; never throws.
    /// </summary>
    public static bool TryRead(string inputJsonPath, out StageInputArtifact? data)
    {
        data = null;
        try
        {
            if (!File.Exists(inputJsonPath))
                return false;
            var json = File.ReadAllText(inputJsonPath);
            if (string.IsNullOrWhiteSpace(json))
                return false;
            data = JsonSerializer.Deserialize<StageInputArtifact>(json, JsonOptions);
            return data is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerate <c>stage{stageNumber}-attempt*.input.json</c> and return the path
    /// with the highest <see cref="RelayAttempt.AttemptNumber"/> (NOT mtime), or
    /// <c>null</c> when the directory is missing or empty.
    /// </summary>
    public static string? LatestPath(string taskDirectory, int stageNumber)
    {
        if (!Directory.Exists(taskDirectory))
            return null;

        var pattern = $"stage{stageNumber}-attempt*.input.json";
        string? bestPath = null;
        var bestAttempt = 0;

        foreach (var path in Directory.EnumerateFiles(taskDirectory, pattern))
        {
            var name = Path.GetFileName(path);
            var attempt = RelayAttempt.AttemptNumber(name);
            if (attempt > bestAttempt)
            {
                bestAttempt = attempt;
                bestPath = path;
            }
        }

        return bestPath;
    }
}
