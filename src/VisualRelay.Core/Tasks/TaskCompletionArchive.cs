using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

internal static partial class TaskCompletionArchive
{
    /// <summary>Performs the task-retirement rename/move on disk BEFORE the git
    /// commit and returns paths to stage plus a rollback delegate. Returns
    /// <c>null</c> when there is nothing to retire.</summary>
    public static RetirementResult? RetireAsync(
        string rootPath,
        RelayConfig config,
        string taskId,
        RelayTaskItem? task)
    {
        if (task is null)
            return null;

        if (!File.Exists(task.MarkdownPath))
        {
            // Source gone — check whether destination already exists.
            var donePath = Path.Combine(task.TaskDirectory, $"DONE-{task.Id}.md");
            if (File.Exists(donePath))
            {
                // Already retired: destination exists, source doesn't.
                return new RetirementResult([], null, false, donePath);
            }

            // Neither source nor DONE- exists. Check the archive destination.
            if (config.ArchiveOnDone)
            {
                // We can't read the batch line from the file (it's gone), but
                // we can scan for an existing DONE- in any completed/batch-N/.
                var archivedPath = FindExistingArchivedPath(rootPath, config.TasksDir, task);
                if (archivedPath is not null)
                {
                    return new RetirementResult([], null, false, archivedPath);
                }
            }

            return null;
        }

        // Read batch number from the original file before any rename.
        string? batch = null;
        if (config.ArchiveOnDone)
        {
            batch = ReadBatchNumber(File.ReadAllText(task.MarkdownPath));
        }

        var doneFilePath = Path.Combine(task.TaskDirectory, $"DONE-{task.Id}.md");

        // Compute the archive destination path (if applicable) before any
        // moves so we can check idempotency against it as well.
        string? archivePath = null;
        if (config.ArchiveOnDone)
        {
            batch ??= HighestCompletedBatch(rootPath, config.TasksDir);
            var completedRoot = Path.Combine(rootPath, config.TasksDir, "completed");
            if (batch is not null)
            {
                var batchDir = Path.Combine(completedRoot, $"batch-{batch}");
                archivePath = task.IsNested
                    ? Path.Combine(batchDir, task.Id)
                    : Path.Combine(batchDir, $"DONE-{task.Id}.md");
            }
            else
            {
                // No batch number available: archive directly under completed/.
                archivePath = task.IsNested
                    ? Path.Combine(completedRoot, task.Id)
                    : Path.Combine(completedRoot, $"DONE-{task.Id}.md");
            }
        }

        // ── Idempotency: destination already exists ──────────────────────
        // Check the archive destination first (it is the authoritative
        // retirement location when archiveOnDone is true), then the flat
        // DONE- path.
        if (archivePath is not null && (File.Exists(archivePath) || Directory.Exists(archivePath)))
        {
            // Both source and archived destination exist. Delete the
            // original (git add -u will stage the deletion) and keep the
            // archived copy as authoritative.
            var originalContent = File.ReadAllText(task.MarkdownPath);
            File.Delete(task.MarkdownPath);

            var additions = CollectAdditions(rootPath, task, archivePath);
            return new RetirementResult(additions, () =>
            {
                if (!File.Exists(task.MarkdownPath))
                {
                    File.WriteAllText(task.MarkdownPath, originalContent);
                }
            }, true, archivePath);
        }

        if (File.Exists(doneFilePath))
        {
            // Both source and flat DONE- exist. This happens when a completed
            // task is resurrected by a git reset/checkout, or when
            // archiveOnDone is false and the DONE- file lingers from a prior
            // run. Delete the original and keep the DONE-.
            var originalContent = File.ReadAllText(task.MarkdownPath);
            File.Delete(task.MarkdownPath);

            var additions = new List<string> { GetPortablePath(rootPath, doneFilePath) };

            return new RetirementResult(additions, () =>
            {
                if (!File.Exists(task.MarkdownPath))
                {
                    File.WriteAllText(task.MarkdownPath, originalContent);
                }
            }, true, doneFilePath);
        }

        // ── Step 1: MarkDone — rename <id>.md → DONE-<id>.md ────────────
        File.Move(task.MarkdownPath, doneFilePath);
        var currentPath = doneFilePath;

        // ── Step 2: Archive if configured and destination was computed ────
        if (archivePath is not null)
        {
            // archivePath was computed above and we already confirmed it
            // doesn't exist. Create parent directory and perform the move.
            var batchDir = Path.GetDirectoryName(archivePath)!;
            Directory.CreateDirectory(batchDir);

            if (task.IsNested)
            {
                Directory.Move(task.TaskDirectory, archivePath);
            }
            else
            {
                File.Move(doneFilePath, archivePath);
            }

            currentPath = archivePath;
        }

        // ── Collect additions (relative paths to new files) ──────────────
        var addList = CollectAdditions(rootPath, task, currentPath);

        // ── Build rollback delegate ──────────────────────────────────────
        var capturedTaskDir = task.TaskDirectory;
        var capturedMarkdownPath = task.MarkdownPath;
        var capturedDonePath = doneFilePath;
        var capturedArchivePath = archivePath;
        var capturedIsNested = task.IsNested;

        return new RetirementResult(addList, () =>
        {
            // Reverse archive move first.
            if (capturedArchivePath is not null)
            {
                if (capturedIsNested && Directory.Exists(capturedArchivePath))
                {
                    Directory.Move(capturedArchivePath, capturedTaskDir);
                }
                else if (File.Exists(capturedArchivePath))
                {
                    File.Move(capturedArchivePath, capturedDonePath);
                }
            }

            // Reverse MarkDone rename.
            if (File.Exists(capturedDonePath) && !File.Exists(capturedMarkdownPath))
            {
                File.Move(capturedDonePath, capturedMarkdownPath);
            }
        }, true, currentPath);
    }

    private static string? FindExistingArchivedPath(string rootPath, string tasksDir, RelayTaskItem task)
    {
        var completedDir = Path.Combine(rootPath, tasksDir, "completed");
        if (!Directory.Exists(completedDir))
            return null;

        // Check directly under completed/ (the no-batch destination).
        if (task.IsNested)
        {
            var direct = Path.Combine(completedDir, task.Id);
            if (Directory.Exists(direct))
                return direct;
        }
        else
        {
            var direct = Path.Combine(completedDir, $"DONE-{task.Id}.md");
            if (File.Exists(direct))
                return direct;
        }

        // Check inside batch-N subdirectories.
        foreach (var batchDir in Directory.EnumerateDirectories(completedDir))
        {
            if (task.IsNested)
            {
                var nested = Path.Combine(batchDir, task.Id);
                if (Directory.Exists(nested))
                    return nested;
            }
            else
            {
                var flat = Path.Combine(batchDir, $"DONE-{task.Id}.md");
                if (File.Exists(flat))
                    return flat;
            }
        }

        return null;
    }

    private static string GetPortablePath(string rootPath, string fullPath)
    {
        // Path.GetRelativePath uses the OS directory separator; normalize to
        // forward slashes for git compatibility.
        return Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
    }

    /// <summary>
    /// Collects relative paths for every file under <paramref name="path"/>
    /// that must be staged. For a flat file this is a single-element list;
    /// for a nested task directory this enumerates all files recursively.
    /// </summary>
    private static List<string> CollectAdditions(string rootPath, RelayTaskItem task, string path)
    {
        var list = new List<string>();
        if (task.IsNested && Directory.Exists(path))
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                list.Add(GetPortablePath(rootPath, file));
            }
        }
        else if (File.Exists(path))
        {
            list.Add(GetPortablePath(rootPath, path));
        }

        return list;
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
