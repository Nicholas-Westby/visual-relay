using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Init;

// Writes a minimal valid .relay/config.json with the given test command and an
// empty logSources array (so the loader treats it as Loaded). Overwrites any
// existing file at that path; callers gate on status before invoking.
public static class RelayConfigWriter
{
    public static string Write(string rootPath, string testCommand)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);

        var json = new JsonObject
        {
            ["testCmd"] = testCommand,
            ["logSources"] = new JsonArray()
        };

        var path = Path.Combine(relayDir, "config.json");
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }
}
