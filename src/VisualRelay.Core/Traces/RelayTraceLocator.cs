namespace VisualRelay.Core.Traces;

public static class RelayTraceLocator
{
    public static string? FindLatestTraceFile(string rootPath, string taskId)
    {
        var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
        if (!Directory.Exists(taskDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(taskDirectory, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }
}

