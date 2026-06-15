using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Init;

// Writes a minimal valid .relay/config.json with the given test command and an
// empty logSources array (so the loader treats it as Loaded). Overwrites any
// existing file at that path; callers gate on status before invoking.
// Pass null for testCommand to write "testCmd": null — the loader treats this
// as Incomplete, which is the deliberate exhaustion signal.
public static class RelayConfigWriter
{
    public static string Write(string rootPath, string? testCommand)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);
        RelayGitignoreWriter.EnsureWritten(rootPath);

        var json = new JsonObject
        {
            ["testCmd"] = testCommand is not null
                ? JsonValue.Create(testCommand)
                : null,
            ["logSources"] = new JsonArray()
        };

        // Auto-detect guard command when guard scripts exist.
        var guardCmd = GuardCommandDetector.Detect(rootPath);
        if (guardCmd is not null)
        {
            json["guardCmd"] = JsonValue.Create(guardCmd);
        }

        // Auto-detect format command for the detected toolchain.
        var formatCmd = FormatCommandDetector.Detect(rootPath);
        if (formatCmd is not null)
        {
            json["formatCmd"] = JsonValue.Create(formatCmd);
        }

        var path = Path.Combine(relayDir, "config.json");
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }

    /// <summary>
    /// Read-modify-write upsert of the <c>bypassSandbox</c> key into
    /// <c>.relay/config.json</c>. Preserves all existing keys (tierProfiles,
    /// baselineVerify, etc.) so toggling the checkbox never clobbers other
    /// settings.
    /// </summary>
    public static void UpsertBypassSandbox(string rootPath, bool bypassSandbox)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);

        var path = Path.Combine(relayDir, "config.json");

        JsonObject json;
        if (File.Exists(path))
        {
            var existing = JsonNode.Parse(File.ReadAllText(path));
            json = existing as JsonObject ?? new JsonObject();
        }
        else
        {
            json = new JsonObject();
        }

        json["bypassSandbox"] = bypassSandbox;

        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// Read-modify-write upsert of the <c>commitProofArtifacts</c> key into
    /// <c>.relay/config.json</c>. Preserves all existing keys (testCmd,
    /// tierProfiles, baselineVerify, etc.) so toggling the checkbox never
    /// clobbers other settings.
    /// </summary>
    public static void UpsertCommitProofArtifacts(string rootPath, bool commitProofArtifacts)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);

        var path = Path.Combine(relayDir, "config.json");

        JsonObject json;
        if (File.Exists(path))
        {
            var existing = JsonNode.Parse(File.ReadAllText(path));
            json = existing as JsonObject ?? new JsonObject();
        }
        else
        {
            json = new JsonObject();
        }

        json["commitProofArtifacts"] = commitProofArtifacts;

        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// Read-modify-write upsert of the <c>boostTurnsTaskIds</c> JSON array into
    /// <c>.relay/config.json</c>. Adds or removes <paramref name="taskId"/>,
    /// de-duplicating entries, while preserving all other keys.
    /// </summary>
    public static void SetTurnBoost(string rootPath, string taskId, bool enabled)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);

        var path = Path.Combine(relayDir, "config.json");

        JsonObject json;
        if (File.Exists(path))
        {
            var existing = JsonNode.Parse(File.ReadAllText(path));
            json = existing as JsonObject ?? new JsonObject();
        }
        else
        {
            json = new JsonObject();
        }

        // Get or create the boostTurnsTaskIds array.
        var array = json["boostTurnsTaskIds"] as JsonArray;
        if (array is null)
        {
            array = new JsonArray();
            json["boostTurnsTaskIds"] = array;
        }

        if (enabled)
        {
            // Add taskId if not already present (de-duplicate).
            if (!array.Any(node => node is JsonValue v && v.TryGetValue(out string? existing) && string.Equals(existing, taskId, StringComparison.Ordinal)))
            {
                array.Add(JsonValue.Create(taskId));
            }
        }
        else
        {
            // Remove all occurrences of taskId.
            var toRemove = new List<JsonNode?>();
            foreach (var node in array)
            {
                if (node is JsonValue v && v.TryGetValue(out string? existing) && string.Equals(existing, taskId, StringComparison.Ordinal))
                {
                    toRemove.Add(node);
                }
            }
            foreach (var node in toRemove)
            {
                array.Remove(node);
            }
        }

        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
