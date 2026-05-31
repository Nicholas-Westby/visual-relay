using System.Text.RegularExpressions;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

internal static partial class TaskCompletionArchive
{
    public static async Task CompleteAsync(
        string rootPath,
        RelayConfig config,
        string taskId,
        RelayTaskItem? task,
        IRelayEventSink eventSink,
        string runId,
        CancellationToken cancellationToken)
    {
        if (task is null || !File.Exists(task.MarkdownPath))
        {
            return;
        }

        try
        {
            var done = MarkDone(task);
            var destination = config.ArchiveOnDone ? Archive(rootPath, config.TasksDir, done) : null;
            await eventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow,
                "info",
                destination is null ? "task_done" : "task_archived",
                runId,
                rootPath,
                taskId,
                11,
                Data: new Dictionary<string, string> { ["path"] = destination ?? done.MarkdownPath }), cancellationToken);
        }
        catch (Exception ex)
        {
            await eventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow,
                "warn",
                "done_rename_failed",
                runId,
                rootPath,
                taskId,
                11,
                Data: new Dictionary<string, string> { ["message"] = ex.Message }), cancellationToken);
        }
    }

    private static RelayTaskItem MarkDone(RelayTaskItem task)
    {
        var donePath = Path.Combine(task.TaskDirectory, $"DONE-{task.Id}.md");
        File.Move(task.MarkdownPath, donePath);
        return task with { MarkdownPath = donePath };
    }

    private static string? Archive(string rootPath, string tasksDir, RelayTaskItem done)
    {
        var batch = ReadBatchNumber(File.ReadAllText(done.MarkdownPath)) ?? HighestCompletedBatch(rootPath, tasksDir);
        if (batch is null)
        {
            return null;
        }

        var batchDirectory = Path.Combine(rootPath, tasksDir, "completed", $"batch-{batch}");
        Directory.CreateDirectory(batchDirectory);
        if (done.IsNested)
        {
            var destination = Path.Combine(batchDirectory, done.Id);
            Directory.Move(done.TaskDirectory, destination);
            return destination;
        }

        var archived = Path.Combine(batchDirectory, $"DONE-{done.Id}.md");
        File.Move(done.MarkdownPath, archived);
        return archived;
    }

    private static string? HighestCompletedBatch(string rootPath, string tasksDir)
    {
        var completed = Path.Combine(rootPath, tasksDir, "completed");
        if (!Directory.Exists(completed))
        {
            return null;
        }

        return Directory.EnumerateDirectories(completed)
            .Select(path => BatchDirectoryRegex().Match(Path.GetFileName(path)))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max() is var max and > 0 ? max.ToString() : null;
    }

    private static string? ReadBatchNumber(string text)
    {
        var match = BatchLineRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^batch:[ \t]*(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex BatchLineRegex();

    [GeneratedRegex(@"^batch-(\d+)$")]
    private static partial Regex BatchDirectoryRegex();
}
