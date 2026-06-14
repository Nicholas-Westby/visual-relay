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
}
