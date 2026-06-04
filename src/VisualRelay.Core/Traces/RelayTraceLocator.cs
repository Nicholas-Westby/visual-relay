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
            .Select(directory => (Directory: directory, Match: ParseDirectory(directory)))
            .Where(item => item.Match is not null)
            .GroupBy(item => item.Match!.Value.Stage)
            .Select(group => group.MaxBy(item => item.Match!.Value.Attempt).Directory)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
            .ToArray();
    }

    private static (int Stage, int Attempt)? ParseDirectory(string directory) =>
        RelayAttempt.TryParse(Path.GetFileName(directory), out var stage, out var attempt)
            ? (stage, attempt)
            : null;
}
