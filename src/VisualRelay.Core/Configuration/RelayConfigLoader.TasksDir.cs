using System.Text.Json;

namespace VisualRelay.Core.Configuration;

public static partial class RelayConfigLoader
{
    /// <summary>
    /// The tasks directory of the project at <paramref name="rootPath"/>, read on its own: the
    /// <c>tasksDir</c> of .relay/config.json, or the default when the file, the key or valid JSON is
    /// missing. The task writer is synchronous and must write where the queue lists tasks from.
    /// </summary>
    public static string ReadTasksDir(string rootPath)
    {
        var fallback = Defaults().TasksDir;
        var path = Path.Combine(rootPath, ".relay", "config.json");
        try
        {
            if (!File.Exists(path))
                return fallback;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? OptionalString(doc.RootElement, "tasksDir", fallback)
                : fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }
}
