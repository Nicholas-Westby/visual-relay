using System.Text;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Tasks;

/// <summary>Where a resolved template came from. Higher layers override lower
/// ones by id: Repo &gt; User &gt; BuiltIn.</summary>
public enum TaskTemplateSource { BuiltIn, User, Repo }

/// <summary>One new-task template: <paramref name="Name"/> labels the dropdown,
/// <paramref name="Title"/> prefills the task-title field, <paramref name="Body"/>
/// prefills the markdown body.</summary>
public sealed record TaskTemplate(
    string Id, string Name, string Title, string Body, TaskTemplateSource Source);

public static class TaskTemplates
{
    public static string ResolveUserTemplatesDir(IEnvironmentAccessor? accessor = null) =>
        Path.Combine(XdgConfig.ResolveConfigDir(accessor), "visual-relay", "templates");

    public static IReadOnlyList<TaskTemplate> Load(string userTemplatesDir, string repoTemplatesDir)
    {
        var byId = new Dictionary<string, TaskTemplate>(StringComparer.OrdinalIgnoreCase);

        // Built-ins — embedded resources packed by the csproj.
        foreach (var resourceName in new[] {
                     "VisualRelay.Core.task-templates.blank.md",
                     "VisualRelay.Core.task-templates.speed-up-automated-tests.md"
                 })
        {
            var id = resourceName;
            const string prefix = "VisualRelay.Core.task-templates.";
            if (id.StartsWith(prefix, StringComparison.Ordinal))
                id = id[prefix.Length..];
            if (id.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                id = id[..^3];

            var content = ReadEmbedded(resourceName);
            byId[id] = Parse(id, content, TaskTemplateSource.BuiltIn);
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
                byId[id] = Parse(id, content, source);
            }
            catch (Exception)
            {
                // unreadable template — skip, never break the dialog
                continue;
            }
        }
    }

    private static string ReadEmbedded(string resourceName)
    {
        var assembly = typeof(TaskTemplates).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded task template '{resourceName}' was not found in "
                + $"{assembly.GetName().Name}. The build must embed packaging/task-templates/.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
