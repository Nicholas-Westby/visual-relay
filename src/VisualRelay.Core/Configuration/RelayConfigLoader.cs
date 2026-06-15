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
                ["vision"] = "vision",
                ["fallback"] = "fallback"
            },
            MaxVerifyLoops: 5,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 0,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2,
            MaxContractRetries: 1,
            // Default false: nono sandbox is required. Set bypassSandbox:true to opt out.
            BypassSandbox: false,
            MaxPlanConcurrency: 10,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000,
            CommitProofArtifacts: true,
            BoostTurnsTaskIds: [],
            DownshiftOnEarlyImplementation: true)
        {
            NewGuardPatterns = ["tools/guards/**/*.sh"]
        };

    public static async Task<RelayConfig> LoadAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var result = await TryLoadAsync(rootPath, cancellationToken);
        return result.Status switch
        {
            RelayConfigStatus.Loaded => result.Config,
            RelayConfigStatus.Defaulted => throw new FileNotFoundException(
                $".relay/config.json not found in {rootPath}",
                Path.Combine(rootPath, ".relay", "config.json")),
            _ => throw new InvalidOperationException(result.Diagnostic ?? "relay config: invalid configuration")
        };
    }

    public static async Task<RelayConfigResult> TryLoadAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var configPath = Path.Combine(rootPath, ".relay", "config.json");
        if (!File.Exists(configPath))
        {
            return new RelayConfigResult(Defaults(), RelayConfigStatus.Defaulted, null);
        }

        JsonDocument doc;
        try
        {
            await using var stream = File.OpenRead(configPath);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return new RelayConfigResult(Defaults(), RelayConfigStatus.Malformed,
                $"relay config: invalid JSON in {configPath}: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!TryGetString(root, "testCmd", out var testCommand) || string.IsNullOrWhiteSpace(testCommand))
            {
                return new RelayConfigResult(Defaults(), RelayConfigStatus.Incomplete,
                    $"relay config: required field testCmd is missing or blank in {configPath}");
            }

            if (!TryReadStringArray(root, "logSources", out var logSources, out var arrayError))
            {
                return new RelayConfigResult(Defaults(), RelayConfigStatus.Malformed, $"{arrayError} in {configPath}");
            }

            var defaults = Defaults(testCommand, logSources);
            var tiers = new Dictionary<string, string>(defaults.TierProfiles);
            if (root.TryGetProperty("tierProfiles", out var tierProfiles) && tierProfiles.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in tierProfiles.EnumerateObject())
                {
                    tiers[property.Name] = property.Value.GetString() ?? tiers.GetValueOrDefault(property.Name, property.Name);
                }
            }

            var firstOutputTiers = new Dictionary<string, int>(defaults.FirstOutputTimeoutMsByTier);
            if (root.TryGetProperty("firstOutputTimeoutMsByTier", out var firstOutputJson) && firstOutputJson.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in firstOutputJson.EnumerateObject())
                {
                    if (property.Value.TryGetInt32(out var ms))
                        firstOutputTiers[property.Name] = ms;
                }
            }

            var inactivityTiers = defaults.InactivityTimeoutMsByTier is not null
                ? new Dictionary<string, int>(defaults.InactivityTimeoutMsByTier)
                : null;
            if (root.TryGetProperty("inactivityTimeoutMsByTier", out var inactivityJson) && inactivityJson.ValueKind == JsonValueKind.Object)
            {
                inactivityTiers ??= new Dictionary<string, int>();
                foreach (var property in inactivityJson.EnumerateObject())
                {
                    if (property.Value.TryGetInt32(out var ms))
                        inactivityTiers[property.Name] = ms;
                }
            }

            var config = defaults with
            {
                TasksDir = OptionalString(root, "tasksDir", defaults.TasksDir),
                TestFileCommand = OptionalString(root, "testFileCmd", defaults.TestFileCommand),
                TierProfiles = tiers,
                MaxVerifyLoops = OptionalInt(root, "maxVerifyLoops", defaults.MaxVerifyLoops),
                MaxStageFailures = OptionalInt(root, "maxStageFailures", defaults.MaxStageFailures),
                MaxTurns = OptionalInt(root, "maxTurns", defaults.MaxTurns),
                BaselineVerify = OptionalBool(root, "baselineVerify", defaults.BaselineVerify),
                ArchiveOnDone = OptionalBool(root, "archiveOnDone", defaults.ArchiveOnDone),
                SubagentTimeoutMilliseconds = OptionalInt(root, "subagentTimeoutMs", defaults.SubagentTimeoutMilliseconds),
                TestTimeoutMilliseconds = OptionalInt(root, "testTimeoutMs", defaults.TestTimeoutMilliseconds),
                FirstOutputTimeoutMsByTier = firstOutputTiers,
                FirstOutputTimeoutMs = OptionalInt(root, "firstOutputTimeoutMs", defaults.FirstOutputTimeoutMs),
                MaxStallRetries = OptionalInt(root, "maxStallRetries", defaults.MaxStallRetries),
                MaxContractRetries = OptionalInt(root, "maxContractRetries", defaults.MaxContractRetries),
                BypassSandbox = OptionalBool(root, "bypassSandbox", defaults.BypassSandbox),
                CommitProofArtifacts = OptionalBool(root, "commitProofArtifacts", defaults.CommitProofArtifacts),
                MaxPlanConcurrency = OptionalInt(root, "maxPlanConcurrency", defaults.MaxPlanConcurrency),
                InactivityTimeoutMsByTier = inactivityTiers,
                InactivityTimeoutMs = OptionalInt(root, "inactivityTimeoutMs", defaults.InactivityTimeoutMs),
                BootstrapFiles = OptionalStringArray(root, "bootstrapFiles"),
                BootstrapCheckCommand = OptionalStringOrNull(root, "bootstrapCheckCmd"),
                GuardCommand = OptionalStringOrNull(root, "guardCmd"),
                FormatCommand = OptionalStringOrNull(root, "formatCmd"),
                BoostTurnsTaskIds = OptionalStringArray(root, "boostTurnsTaskIds"),
                NewGuardPatterns = OptionalStringArray(root, "newGuardPatterns", defaults.NewGuardPatterns),
                DownshiftOnEarlyImplementation = OptionalBool(root, "downshiftOnEarlyImplementation", defaults.DownshiftOnEarlyImplementation)
            };
            return new RelayConfigResult(config, RelayConfigStatus.Loaded, null);
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // Optional array: absent -> empty (ok); present-but-not-array -> error.
    private static bool TryReadStringArray(JsonElement root, string name, out IReadOnlyList<string> values, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(name, out var element))
        {
            values = [];
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            values = [];
            error = $"relay config: {name} must be an array";
            return false;
        }

        values = element.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
        return true;
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

    // Absent or non-array → empty list; present array → string values.
    private static IReadOnlyList<string> OptionalStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
            return [];
        return element.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
    }

    // Absent or non-array → fallback; present array → string values.
    private static IReadOnlyList<string> OptionalStringArray(JsonElement root, string name, IReadOnlyList<string> fallback)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
            return fallback;
        return element.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
    }

    // Absent or non-string → null; present string → value.
    private static string? OptionalStringOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
