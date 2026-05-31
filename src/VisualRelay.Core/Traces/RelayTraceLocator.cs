namespace VisualRelay.Core.Traces;

public static class RelayTraceLocator
{
    public static string? FindLatestTraceFile(string rootPath, string taskId)
    {
        return FindTraceFiles(rootPath, taskId)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    public static IReadOnlyList<string> FindTraceFiles(string rootPath, string taskId, int? stageNumber = null)
    {
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        if (!Directory.Exists(taskDirectory))
        {
            return [];
        }

        var pattern = stageNumber is null ? "stage*-attempt*" : $"stage{stageNumber}-attempt*";
        return Directory.EnumerateDirectories(taskDirectory, pattern, SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
            .ToArray();
    }
}
