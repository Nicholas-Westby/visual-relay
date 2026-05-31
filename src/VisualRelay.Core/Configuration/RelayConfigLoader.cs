using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Configuration;

public static class RelayConfigLoader
{
    public static RelayConfig Defaults(string testCommand = "bun test", IReadOnlyList<string>? logSources = null) =>
        new(
            TasksDir: "llm-tasks",
            TestCommand: testCommand,
            TestFileCommand: "bun test {files}",
            LogSources: logSources ?? [],
            TierProfiles: new Dictionary<string, string>
            {
                ["cheap"] = "cheap",
                ["balanced"] = "balanced",
                ["frontier"] = "frontier",
                ["vision"] = "vision"
            },
            MaxVerifyLoops: 5,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000);

    public static async Task<RelayConfig> LoadAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var configPath = Path.Combine(rootPath, ".relay", "config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($".relay/config.json not found in {rootPath}", configPath);
        }

        await using var stream = File.OpenRead(configPath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        var testCommand = RequiredString(root, "testCmd");
        var defaults = Defaults(testCommand, ReadStringArray(root, "logSources"));
        var tiers = new Dictionary<string, string>(defaults.TierProfiles);

        if (root.TryGetProperty("tierProfiles", out var tierProfiles))
        {
            foreach (var property in tierProfiles.EnumerateObject())
            {
                tiers[property.Name] = property.Value.GetString() ?? tiers.GetValueOrDefault(property.Name, property.Name);
            }
        }

        return defaults with
        {
            TasksDir = OptionalString(root, "tasksDir", defaults.TasksDir),
            TestFileCommand = OptionalString(root, "testFileCmd", defaults.TestFileCommand),
            TierProfiles = tiers,
            MaxVerifyLoops = OptionalInt(root, "maxVerifyLoops", defaults.MaxVerifyLoops),
            MaxStageFailures = OptionalInt(root, "maxStageFailures", defaults.MaxStageFailures),
            MaxTurns = OptionalInt(root, "maxTurns", defaults.MaxTurns),
            BaselineVerify = OptionalBool(root, "baselineVerify", defaults.BaselineVerify),
            ArchiveOnDone = OptionalBool(root, "archiveOnDone", defaults.ArchiveOnDone),
            SubagentTimeoutMilliseconds = OptionalInt(root, "subagentTimeoutMs", defaults.SubagentTimeoutMilliseconds)
        };
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"relay config: required field {name} is missing");
        }

        return value.GetString()!;
    }

    private static string OptionalString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int OptionalInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static bool OptionalBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"relay config: {name} must be an array");
        }

        return value.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
    }
}
