namespace VisualRelay.Core.Init;

public static partial class RelayConfigWriter
{
    /// <summary>
    /// Read-modify-write upsert of the <c>tasksDir</c> key into <c>.relay/config.json</c>.
    /// Preserves all existing keys.
    /// </summary>
    public static void UpsertTasksDir(string rootPath, string tasksDir)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);
        var json = ReadOrCreateConfig(relayDir, out var path);

        json["tasksDir"] = tasksDir;

        File.WriteAllText(path, ConfigText(path, json));
    }
}
