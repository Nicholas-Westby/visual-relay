using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

/// <summary>
/// Pure filesystem writer for task creation, editing, and attachment management.
/// Mirrors the static-writer pattern established by <see cref="Init.RelayConfigWriter"/>.
/// </summary>
public static class RelayTaskWriter
{
    /// <summary>Converts a free-text title into a filesystem-safe kebab-case slug.</summary>
    public static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // Lowercase, replace any non-alphanumeric sequence with a single hyphen.
        var slug = Regex.Replace(title.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-");
        // Trim leading/trailing hyphens that might result from punctuation at edges.
        slug = slug.Trim('-');
        // Collapse runs of hyphens.
        slug = Regex.Replace(slug, @"-{2,}", "-");
        return slug;
    }

    /// <summary>
    /// Validates a slug against naming rules. Returns null when the slug is acceptable;
    /// otherwise an error message suitable for display.
    /// </summary>
    public static string? ValidateSlug(string slug, string? rootPath = null)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Slug is empty — enter a title to derive one.";
        }

        // Reserved prefixes — check before case rules since the callers
        // may pass un-normalised slugs.
        if (slug.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ||
            slug.StartsWith("IGNORE-", StringComparison.OrdinalIgnoreCase))
        {
            return $"Slug \"{slug}\" starts with a reserved prefix (DONE-/IGNORE-). Choose a different name.";
        }

        // Must be only lowercase letters, digits, and hyphens.
        if (!Regex.IsMatch(slug, @"^[a-z0-9]+(-[a-z0-9]+)*$"))
        {
            return $"Slug \"{slug}\" contains unsafe characters. Use lowercase letters, digits, and hyphens only.";
        }

        // Collision check (optional rootPath parameter).
        if (rootPath is not null)
        {
            var tasksDir = Path.Combine(rootPath, "llm-tasks");
            var flatPath = Path.Combine(tasksDir, $"{slug}.md");
            var nestedDir = Path.Combine(tasksDir, slug);

            if (File.Exists(flatPath) || Directory.Exists(nestedDir))
            {
                return $"A task named \"{slug}\" already exists. Choose a different name.";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the canonical nested markdown path:
    /// <c>llm-tasks/&lt;slug&gt;/&lt;slug&gt;.md</c>.
    /// </summary>
    private static string BuildNestedMarkdownPath(string tasksDir, string slug) =>
        Path.Combine(tasksDir, slug, $"{slug}.md");

    /// <summary>
    /// Creates a nested task file: <c>llm-tasks/&lt;slug&gt;/&lt;slug&gt;.md</c>.
    /// Returns the full path to the written file.
    /// Throws <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/>
    /// on invalid input so callers can surface the message.
    /// </summary>
    public static async Task<string> CreateAsync(string rootPath, string slug, string markdown)
    {
        var error = ValidateSlug(slug, rootPath);
        if (error is not null)
        {
            // Validation that would apply regardless of rootPath (empty/unsafe/reserved)
            // throws ArgumentException. Collision throws InvalidOperationException.
            if (error.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("unsafe", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("reserved", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(error);
            }

            throw new InvalidOperationException(error);
        }

        var tasksDir = Path.Combine(rootPath, "llm-tasks");
        Directory.CreateDirectory(tasksDir);

        var nestedDir = Path.Combine(tasksDir, slug);
        Directory.CreateDirectory(nestedDir);
        var filePath = BuildNestedMarkdownPath(tasksDir, slug);
        await File.WriteAllTextAsync(filePath, markdown);
        return filePath;
    }

    /// <summary>
    /// Overwrites a task's markdown file with new content.
    /// </summary>
    public static async Task SaveAsync(RelayTaskItem task, string markdown)
    {
        await File.WriteAllTextAsync(task.MarkdownPath, markdown);
    }

    /// <summary>
    /// Converts a flat task into the nested layout. Creates
    /// <c>llm-tasks/&lt;slug&gt;/&lt;slug&gt;.md</c>, moves the existing flat
    /// file's content, and deletes the original flat file.
    /// Returns the new markdown path. No-ops when the task is already nested.
    /// </summary>
    public static async Task<string> PromoteToNestedAsync(string rootPath, RelayTaskItem task)
    {
        if (task.IsNested)
        {
            return task.MarkdownPath;
        }

        var tasksDir = Path.Combine(rootPath, "llm-tasks");
        var nestedDir = Path.Combine(tasksDir, task.Id);
        Directory.CreateDirectory(nestedDir);

        var newMarkdownPath = BuildNestedMarkdownPath(tasksDir, task.Id);

        // Read existing content before we delete the flat file.
        var content = await File.ReadAllTextAsync(task.MarkdownPath);
        await File.WriteAllTextAsync(newMarkdownPath, content);

        File.Delete(task.MarkdownPath);
        return newMarkdownPath;
    }

    /// <summary>
    /// Copies a source file into the task's directory. If the task is flat,
    /// promotes it to nested first so subsequent attachments land in the folder.
    /// Returns the full destination path.
    /// </summary>
    public static async Task<string> AddAttachmentAsync(RelayTaskItem task, string sourceFilePath)
    {
        // Resolve the target directory; promote if necessary.
        string taskDir;
        if (!task.IsNested)
        {
            // Need the rootPath. Derive it from MarkdownPath: the parent of
            // the tasks directory is the root.
            var tasksDir = Path.GetDirectoryName(task.MarkdownPath)!;
            var rootPath = Path.GetDirectoryName(tasksDir)!; // tasksDir is llm-tasks/
            var newMarkdownPath = await PromoteToNestedAsync(rootPath, task);
            taskDir = Path.GetDirectoryName(newMarkdownPath)!;
        }
        else
        {
            taskDir = task.TaskDirectory;
        }

        var fileName = Path.GetFileName(sourceFilePath);
        var destinationPath = Path.Combine(taskDir, fileName);

        // If a file with that name already exists, overwrite it.
        await using var sourceStream = File.OpenRead(sourceFilePath);
        await using var destStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destStream);

        return destinationPath;
    }

    /// <summary>
    /// Deletes an attachment file. Returns true if the file existed and was deleted.
    /// </summary>
    public static bool RemoveAttachment(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        File.Delete(filePath);
        return true;
    }
}
