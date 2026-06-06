using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

public sealed class RelayTaskRepository
{
    private static readonly HashSet<string> SkippedDirectories = ["completed", "_ideation"];
    private static readonly HashSet<string> TextExtensions = ["html", "htm", "txt", "json", "csv", "tsv", "xml", "yaml", "yml", "log", "md"];
    private const int PerFileContextLimit = 8_000;
    private const int TotalContextLimit = 24_000;

    public RelayTaskRepository(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public async Task<IReadOnlyList<RelayTaskItem>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        return (await ListAsync(includeNeedsReview: false, cancellationToken))
            .Where(task => !task.NeedsReview)
            .ToArray();
    }

    public async Task<IReadOnlyList<RelayTaskItem>> ListAsync(
        bool includeNeedsReview = true,
        CancellationToken cancellationToken = default)
    {
        var loaded = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
        if (loaded.Status == RelayConfigStatus.Malformed)
        {
            return [];
        }

        var config = loaded.Config;
        var tasksRoot = Path.Combine(RootPath, config.TasksDir);
        if (!Directory.Exists(tasksRoot))
        {
            return [];
        }

        var tasks = new List<RelayTaskItem>();
        Walk(tasksRoot, tasksRoot, tasks);
        return tasks
            .Select(AttachReviewState)
            .Select(AttachRunMetrics)
            .Where(task => includeNeedsReview || !task.NeedsReview)
            .OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<RelayTaskItem>> ListCompletedAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
        if (loaded.Status == RelayConfigStatus.Malformed)
        {
            return [];
        }

        var config = loaded.Config;
        var completedRoot = Path.Combine(RootPath, config.TasksDir, "completed");
        if (!Directory.Exists(completedRoot))
        {
            return [];
        }

        // Group DONE-*.md files by directory so each folder yields exactly
        // one task with the remaining DONE-*.md files as siblings.
        var allFiles = Directory.EnumerateFiles(completedRoot, "DONE-*.md", SearchOption.AllDirectories);
        var byDirectory = allFiles
            .GroupBy(Path.GetDirectoryName)
            .ToDictionary(g => g.Key!, g => g.ToArray());

        var tasks = new List<RelayTaskItem>();
        foreach (var (directory, files) in byDirectory)
        {
            var canonical = FindCanonicalArchivedPath(directory!, files);
            if (canonical is not null)
            {
                tasks.Add(ArchivedTaskFromPath(completedRoot, canonical, files));
            }
        }

        return tasks
            .Select(AttachRunMetrics)
            .OrderByDescending(task => File.GetLastWriteTimeUtc(task.MarkdownPath))
            .ToArray();
    }

    /// <summary>Picks the canonical DONE-*.md for a directory: the file whose
    /// id matches the directory name, or the shortest filename as fallback.</summary>
    private static string? FindCanonicalArchivedPath(string directory, string[] doneFiles)
    {
        var dirName = Path.GetFileName(directory);
        foreach (var file in doneFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var id = fileName.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase)
                ? fileName[5..] : fileName;
            if (string.Equals(id, dirName, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return doneFiles.OrderBy(f => f.Length).FirstOrDefault();
    }

    public async Task<RelayTaskInput> ReadTaskInputAsync(RelayTaskItem task, CancellationToken cancellationToken = default)
    {
        var markdown = await File.ReadAllTextAsync(task.MarkdownPath, cancellationToken);
        markdown = StripBatchLine(markdown);
        return new RelayTaskInput(markdown, BuildContext(task.SiblingPaths));
    }

    private static void Walk(string tasksRoot, string directory, List<RelayTaskItem> tasks)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (IsSkippedName(name))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                if (!SkippedDirectories.Contains(name))
                {
                    EmitSingleTaskFromFolder(tasksRoot, path, tasks);
                }

                continue;
            }

            // Top-level .md files are flat tasks.
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(name);
                tasks.Add(new RelayTaskItem(id, path, directory, false, []));
            }
        }
    }

    /// <summary>
    /// Emits exactly one task for a subfolder. The canonical markdown is the
    /// folder-named .md; fallback is the first .md in the folder. All other
    /// entries (including other .md files) become siblings.
    /// </summary>
    private static void EmitSingleTaskFromFolder(string tasksRoot, string folderPath, List<RelayTaskItem> tasks)
    {
        var folderName = Path.GetFileName(folderPath);
        var entries = Directory.EnumerateFiles(folderPath).ToArray();

        // Find the canonical folder-named .md file.
        var canonicalPath = Path.Combine(folderPath, $"{folderName}.md");
        string? markdownPath;
        if (File.Exists(canonicalPath))
        {
            markdownPath = canonicalPath;
        }
        else
        {
            // Legacy: pick the first .md file in the folder.
            markdownPath = entries.FirstOrDefault(
                f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        }

        if (markdownPath is null)
        {
            // No markdown in this folder — skip it.
            return;
        }

        var taskId = Path.GetFileNameWithoutExtension(markdownPath);

        // Everything except the task markdown itself is a sibling (attachment).
        var siblings = entries
            .Where(f => !string.Equals(f, markdownPath, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        tasks.Add(new RelayTaskItem(taskId, markdownPath, folderPath, true, siblings));
    }

    private RelayTaskItem AttachReviewState(RelayTaskItem task)
    {
        var reviewFile = Path.Combine(RootPath, ".relay", task.Id, "NEEDS-REVIEW");
        if (!File.Exists(reviewFile))
        {
            return task;
        }

        var reason = File.ReadLines(reviewFile).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return task with { ReviewReason = reason ?? "Needs review" };
    }

    private RelayTaskItem AttachRunMetrics(RelayTaskItem task)
    {
        var metric = RelayRunHistory.ReadTaskMetric(RootPath, task.Id);
        return task with
        {
            CostUsd = metric.CostUsd,
            DurationSeconds = metric.DurationSeconds,
            CompletedStageCount = metric.CompletedStageCount
        };
    }

    private static RelayTaskItem ArchivedTaskFromPath(string completedRoot, string markdownPath, string[] allDoneFilesInDir)
    {
        var directory = Path.GetDirectoryName(markdownPath)!;
        var fileName = Path.GetFileNameWithoutExtension(markdownPath);
        var id = fileName.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ? fileName[5..] : fileName;
        var batchName = FindBatchName(completedRoot, markdownPath);
        var batchDirectory = batchName is null ? completedRoot : Path.Combine(completedRoot, batchName);
        // All files except the task markdown are siblings — including .md attachments.
        var siblings = Directory.EnumerateFiles(directory)
            .Where(file => !string.Equals(file, markdownPath, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new RelayTaskItem(
            id,
            markdownPath,
            directory,
            !string.Equals(directory, batchDirectory, StringComparison.Ordinal),
            siblings,
            IsArchived: true,
            ArchiveBatch: batchName);
    }

    private static string? FindBatchName(string completedRoot, string path)
    {
        var relative = Path.GetRelativePath(completedRoot, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
        return first?.StartsWith("batch-", StringComparison.OrdinalIgnoreCase) == true ? first : null;
    }

    private static bool IsSkippedName(string name) =>
        name.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IGNORE-", StringComparison.OrdinalIgnoreCase);

    private static string StripBatchLine(string markdown)
    {
        if (!markdown.StartsWith("batch:", StringComparison.OrdinalIgnoreCase))
        {
            return markdown;
        }

        var firstNewline = markdown.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewline < 0)
        {
            return string.Empty;
        }

        var rest = markdown[(firstNewline + 1)..];
        return rest.StartsWith('\n') ? rest[1..] : rest;
    }

    private static string? BuildContext(IReadOnlyList<string> siblingPaths)
    {
        if (siblingPaths.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        var used = 0;
        foreach (var path in siblingPaths)
        {
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var size = new FileInfo(path).Length;
            if (TextExtensions.Contains(extension) && size <= PerFileContextLimit && used < TotalContextLimit)
            {
                var body = File.ReadAllText(path);
                if (used + body.Length > TotalContextLimit)
                {
                    body = string.Concat(body.AsSpan(0, TotalContextLimit - used), "\n...(truncated)");
                }

                used += body.Length;
                parts.Add($"### {name} ({size} bytes){Environment.NewLine}{body}");
            }
            else
            {
                parts.Add($"### {name} ({size} bytes, not inlined)");
            }
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", parts);
    }
}
