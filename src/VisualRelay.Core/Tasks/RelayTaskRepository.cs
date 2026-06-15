using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

public sealed class RelayTaskRepository
{
    private static readonly HashSet<string> SkippedDirectories = ["completed", "_ideation"];
    // PerFileContextLimit and TotalContextLimit live in TaskContentHelper.

    public RelayTaskRepository(string rootPath)
    {
        RootPath = rootPath;
    }

    private string RootPath { get; }

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
        Walk(tasksRoot, tasks);
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
        var tasksRoot = Path.Combine(RootPath, config.TasksDir);
        var tasks = new List<RelayTaskItem>();
        // Top-level DONE-*.md files (stranded when batch move was skipped),
        // and DONE-*.md files in regular subdirectories (folder tasks where the
        // only .md is DONE-prefixed — these are skipped by EmitSingleTaskFromFolder).
        if (Directory.Exists(tasksRoot))
        {
            foreach (var file in Directory.EnumerateFiles(tasksRoot, "DONE-*.md", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var id = name.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
                tasks.Add(new RelayTaskItem(id, file, tasksRoot, false, [],
                    IsArchived: true, ArchiveBatch: null));
            }

            foreach (var subdir in Directory.EnumerateDirectories(tasksRoot))
            {
                var dirName = Path.GetFileName(subdir);
                if (SkippedDirectories.Contains(dirName) || IsSkippedName(dirName))
                    continue;

                var doneFiles = Directory.EnumerateFiles(subdir, "DONE-*.md", SearchOption.TopDirectoryOnly).ToArray();
                if (doneFiles.Length == 0)
                    continue;

                // Skip folders that still have an active (non-skipped) .md file —
                // these are pending folders with DONE residue, not all-DONE folders.
                var hasActiveMd = Directory.EnumerateFiles(subdir, "*.md", SearchOption.TopDirectoryOnly)
                    .Any(f => !IsSkippedName(Path.GetFileName(f)));
                if (hasActiveMd)
                    continue;

                var canonical = FindCanonicalArchivedPath(subdir, doneFiles);
                if (canonical is null)
                    continue;

                var id = Path.GetFileNameWithoutExtension(canonical);
                id = id.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ? id[5..] : id;

                // Guard against double-count: skip if already added as top-level DONE-*.md.
                if (tasks.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var siblings = Directory.EnumerateFiles(subdir)
                    .Where(file => !string.Equals(file, canonical, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                tasks.Add(new RelayTaskItem(
                    id,
                    canonical,
                    subdir,
                    IsNested: true,
                    siblings,
                    IsArchived: true,
                    ArchiveBatch: null));
            }
        }

        // DONE-*.md files under completed/batch-n/ (grouped by directory).
        var completedRoot = Path.Combine(tasksRoot, "completed");
        if (Directory.Exists(completedRoot))
        {
            var allFiles = Directory.EnumerateFiles(completedRoot, "DONE-*.md", SearchOption.AllDirectories);
            var byDirectory = allFiles
                .GroupBy(Path.GetDirectoryName)
                .ToDictionary(g => g.Key!, g => g.ToArray());
            foreach (var (directory, files) in byDirectory)
            {
                var canonical = FindCanonicalArchivedPath(directory!, files);
                if (canonical is not null)
                    tasks.Add(ArchivedTaskFromPath(completedRoot, canonical));
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
        markdown = TaskContentHelper.StripBatchLine(markdown);
        return new RelayTaskInput(markdown, TaskContentHelper.BuildContext(task.SiblingPaths));
    }

    private static void Walk(string directory, List<RelayTaskItem> tasks)
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
                    EmitSingleTaskFromFolder(path, tasks);
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
    private static void EmitSingleTaskFromFolder(string folderPath, List<RelayTaskItem> tasks)
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
                f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !IsSkippedName(Path.GetFileName(f)));
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

    private static RelayTaskItem ArchivedTaskFromPath(string completedRoot, string markdownPath)
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

}
