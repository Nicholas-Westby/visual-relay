using System.Text;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Tasks;

/// <summary>Where a resolved template came from. Higher layers override lower
/// ones by id: Repo &gt; User &gt; BuiltIn.</summary>
public enum TaskTemplateSource { BuiltIn, User, Repo }

/// <summary>A file shipped beside a template and copied into the task folder
/// when a task is created from that template.</summary>
public sealed record TaskTemplateAttachment(string FileName, byte[] Content);

/// <summary>One new-task template: <paramref name="Name"/> labels the dropdown,
/// <paramref name="Title"/> prefills the task-title field, <paramref name="Body"/>
/// prefills the markdown body.</summary>
public sealed record TaskTemplate(
    string Id, string Name, string Title, string Body, TaskTemplateSource Source)
{
    /// <summary>Files from the template's sibling directory (named after the
    /// template id). The winning layer's set is taken whole — never merged
    /// across layers.</summary>
    public IReadOnlyList<TaskTemplateAttachment> Attachments { get; init; } = [];
}

public static class TaskTemplates
{
    private const string EmbeddedPrefix = "VisualRelay.Core.task-templates.";

    public static string ResolveUserTemplatesDir(IEnvironmentAccessor? accessor = null) =>
        Path.Combine(XdgConfig.ResolveConfigDir(accessor), "visual-relay", "templates");

    public static IReadOnlyList<TaskTemplate> Load(string userTemplatesDir, string repoTemplatesDir)
    {
        var byId = new Dictionary<string, TaskTemplate>(StringComparer.OrdinalIgnoreCase);

        // Built-ins — embedded resources packed by the csproj. A resource name
        // without '/' is a template ("<id>.md"); one with '/' is an attachment
        // ("<id>/<fileName>") of the template named before the '/'.
        var resourceNames = typeof(TaskTemplates).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        foreach (var resourceName in resourceNames)
        {
            var rest = resourceName[EmbeddedPrefix.Length..];
            if (rest.Contains('/') || !rest.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;

            var id = rest[..^3];
            var content = ReadEmbedded(resourceName);
            byId[id] = Parse(id, content, TaskTemplateSource.BuiltIn) with
            {
                Attachments = LoadEmbeddedAttachments(resourceNames, $"{EmbeddedPrefix}{id}/"),
            };
        }

        // User layer overlays built-ins.
        OverlayDirectory(byId, userTemplatesDir, TaskTemplateSource.User);

        // Repo layer overlays user.
        OverlayDirectory(byId, repoTemplatesDir, TaskTemplateSource.Repo);

        return byId.Values
            .OrderBy(t => string.Equals(t.Id, "blank", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static TaskTemplate Parse(string id, string content, TaskTemplateSource source)
    {
        content = content.Replace("\r\n", "\n");
        var lines = content.Split('\n');
        var name = id;
        var title = string.Empty;
        var bodyStart = 0;

        if (lines.Length > 0 && lines[0] == "---")
        {
            var closeIdx = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i] == "---")
                {
                    closeIdx = i;
                    break;
                }
            }

            if (closeIdx >= 0)
            {
                for (var i = 1; i < closeIdx; i++)
                {
                    var colonIdx = lines[i].IndexOf(':');
                    if (colonIdx < 0) continue;

                    var key = lines[i][..colonIdx].Trim().ToLowerInvariant();
                    var value = lines[i][(colonIdx + 1)..].Trim();
                    switch (key)
                    {
                        case "name": name = value; break;
                        case "title": title = value; break;
                    }
                }

                bodyStart = closeIdx + 1;
            }
            // else: unclosed frontmatter — whole content is body, Name/Title fall back
        }

        var body = bodyStart > 0
            ? string.Join('\n', lines.Skip(bodyStart)).TrimStart('\n')
            : content;

        return new TaskTemplate(id, name, title, body, source);
    }

    private static void OverlayDirectory(
        Dictionary<string, TaskTemplate> byId, string dir, TaskTemplateSource source)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var content = File.ReadAllText(file);
                byId[id] = Parse(id, content, source) with
                {
                    Attachments = LoadDirectoryAttachments(Path.Combine(dir, id)),
                };
            }
            catch (Exception)
            {
                // unreadable template — skip, never break the dialog
            }
        }
    }

    /// <summary>Writes every attachment of <paramref name="template"/> into
    /// <paramref name="taskDirectory"/>. Attachment names are flattened to their
    /// file name so a hostile entry can never escape the task folder.</summary>
    public static async Task WriteAttachmentsAsync(string taskDirectory, TaskTemplate template)
    {
        foreach (var attachment in template.Attachments)
        {
            var fileName = Path.GetFileName(attachment.FileName);
            if (string.IsNullOrEmpty(fileName))
                continue;

            await File.WriteAllBytesAsync(Path.Combine(taskDirectory, fileName), attachment.Content);
        }
    }

    private static IReadOnlyList<TaskTemplateAttachment> LoadDirectoryAttachments(string attachmentsDir)
    {
        if (!Directory.Exists(attachmentsDir))
            return [];

        var attachments = new List<TaskTemplateAttachment>();
        foreach (var file in Directory.EnumerateFiles(attachmentsDir, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith('.'))
                continue;

            try
            {
                attachments.Add(new TaskTemplateAttachment(fileName, File.ReadAllBytes(file)));
            }
            catch (Exception)
            {
                // unreadable attachment — skip it, keep the template usable
            }
        }

        return attachments;
    }

    private static IReadOnlyList<TaskTemplateAttachment> LoadEmbeddedAttachments(
        IReadOnlyList<string> resourceNames, string attachmentPrefix)
    {
        var attachments = new List<TaskTemplateAttachment>();
        foreach (var name in resourceNames)
        {
            if (!name.StartsWith(attachmentPrefix, StringComparison.Ordinal))
                continue;

            var fileName = name[attachmentPrefix.Length..];
            if (fileName.Length == 0 || fileName.Contains('/'))
                continue;

            attachments.Add(new TaskTemplateAttachment(fileName, ReadEmbeddedBytes(name)));
        }

        return attachments
            .OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ReadEmbedded(string resourceName)
    {
        using var stream = OpenEmbedded(resourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadEmbeddedBytes(string resourceName)
    {
        using var stream = OpenEmbedded(resourceName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream OpenEmbedded(string resourceName)
    {
        var assembly = typeof(TaskTemplates).Assembly;
        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded task template '{resourceName}' was not found in "
                + $"{assembly.GetName().Name}. The build must embed packaging/task-templates/.");
    }
}
