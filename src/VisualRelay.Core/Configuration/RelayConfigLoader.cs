using System.Text.Json;
using VisualRelay.Domain;
namespace VisualRelay.Core.Configuration;

public static partial class RelayConfigLoader
{
    public static RelayConfig Defaults(string testCommand = "bun test", IReadOnlyList<string>? logSources = null) =>
        new(
            TasksDir: "llm-tasks",
            TestCommand: testCommand,
            TestFileCommand: testCommand,
            LogSources: logSources ?? [],
            TierProfiles: new Dictionary<string, string>
            {
                ["cheap"] = "cheap",
                ["balanced"] = "balanced",
                ["frontier"] = "frontier",
                ["vision"] = "vision",
                ["fallback"] = "fallback"
            },
            EnableFixVerify: true,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 2_700_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            FirstOutputTimeoutMs: 660_000,
            MaxPlanConcurrency: 10,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000,
            OutputSilenceTimeoutMsByTier: null,
            OutputSilenceTimeoutMs: 0,
            CommitProofArtifacts: true,
            BoostTurnsTaskIds: [],
            SkipTestsTaskIds: [],
            DownshiftOnEarlyImplementation: true,
            RetryFlakyVerify: true,
            TierModelOverrides: null)
        {
            NewGuardPatterns = ["tools/guards/**/*.sh"],
            TestPaths = []
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

            // Per-tier dictionary parse helper: defaults + json overrides.
            static Dictionary<string, int>? ParseTierDict(JsonElement root, string prop, IReadOnlyDictionary<string, int>? defaults)
            {
                var map = defaults is not null ? new Dictionary<string, int>(defaults) : null;
                if (root.TryGetProperty(prop, out var json) && json.ValueKind == JsonValueKind.Object)
                {
                    map ??= new Dictionary<string, int>();
                    foreach (var p in json.EnumerateObject())
                        if (p.Value.TryGetInt32(out var ms)) map[p.Name] = ms;
                }
                return map;
            }

            var inactivityTiers = ParseTierDict(root, "inactivityTimeoutMsByTier", defaults.InactivityTimeoutMsByTier);
            var outputSilenceTiers = ParseTierDict(root, "outputSilenceTimeoutMsByTier", defaults.OutputSilenceTimeoutMsByTier);

            // Read and validate sandboxExtraAllowPaths.
            IReadOnlyList<string>? sandboxExtraAllowPaths = null;
            if (root.TryGetProperty("sandboxExtraAllowPaths", out var extraPathsElement))
            {
                if (extraPathsElement.ValueKind == JsonValueKind.Array)
                {
                    var rawPaths = extraPathsElement.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!.Trim())
                        .ToList();

                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var validated = new List<string>(rawPaths.Count);
                    foreach (var raw in rawPaths)
                    {
                        // Expand ~ and $HOME.
                        var expanded = raw.StartsWith("~/") || raw == "~"
                            ? Path.Combine(home, raw.Length > 2 ? raw[2..].TrimStart('/') : string.Empty)
                            : raw.Replace("$HOME", home, StringComparison.Ordinal);

                        // Reject .. traversal.
                        if (expanded.Contains(".."))
                        {
                            return new RelayConfigResult(defaults, RelayConfigStatus.Malformed,
                                $"relay config: sandboxExtraAllowPaths entry contains '..' (path traversal rejected): \"{raw}\" in {configPath}");
                        }

                        // Normalize to absolute path.
                        var normalized = Path.GetFullPath(expanded);

                        // Require resolution under $HOME or workspace root.
                        var normalizedHome = Path.GetFullPath(home);
                        var normalizedRoot = Path.GetFullPath(rootPath);
                        var underHome = normalized.StartsWith(normalizedHome + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                        || normalized == normalizedHome;
                        var underRoot = normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                        || normalized == normalizedRoot;

                        if (!underHome && !underRoot)
                        {
                            return new RelayConfigResult(defaults, RelayConfigStatus.Malformed,
                                $"relay config: sandboxExtraAllowPaths entry must resolve under $HOME or workspace root, got: \"{raw}\" → \"{normalized}\" in {configPath}");
                        }
                        // Reject entries that resolve into known-sensitive subtrees.
                        var sensitiveSubtrees = new[] {
                            Path.Combine(normalizedHome, ".ssh"), Path.Combine(normalizedHome, ".gnupg"),
                            Path.Combine(normalizedHome, ".aws"), Path.Combine(normalizedHome, ".config", "gh"),
                            Path.Combine(normalizedHome, "Library", "Keychains"),
                            Path.Combine(normalizedHome, ".bashrc"), Path.Combine(normalizedHome, ".zshrc"),
                            Path.Combine(normalizedHome, ".profile"), Path.Combine(normalizedHome, ".bash_profile"),
                            Path.Combine(normalizedHome, ".zprofile") };
                        foreach (var subtree in sensitiveSubtrees)
                            if (normalized == subtree || normalized.StartsWith(subtree + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                                return new RelayConfigResult(defaults, RelayConfigStatus.Malformed,
                                    $"relay config: sandboxExtraAllowPaths entry resolves into sensitive subtree \"{subtree}\": \"{raw}\" → \"{normalized}\" in {configPath}");

                        validated.Add(normalized);
                    }

                    sandboxExtraAllowPaths = validated;
                }
                else
                {
                    return new RelayConfigResult(defaults, RelayConfigStatus.Malformed,
                        $"relay config: sandboxExtraAllowPaths must be an array in {configPath}");
                }
            }

            IReadOnlyDictionary<string, string>? tierModelOverrides =
                root.TryGetProperty("tierModelOverrides", out var tmo)
                    ? TryParseTierModelOverrides(tmo) : null;

            var config = defaults with
            {
                TasksDir = OptionalString(root, "tasksDir", defaults.TasksDir),
                TestFileCommand = OptionalString(root, "testFileCmd", defaults.TestFileCommand),
                TierProfiles = tiers,
                EnableFixVerify = OptionalBool(root, "enableFixVerify", defaults.EnableFixVerify),
                MaxStageFailures = OptionalInt(root, "maxStageFailures", defaults.MaxStageFailures),
                MaxTurns = OptionalInt(root, "maxTurns", defaults.MaxTurns),
                BaselineVerify = OptionalBool(root, "baselineVerify", defaults.BaselineVerify),
                ArchiveOnDone = OptionalBool(root, "archiveOnDone", defaults.ArchiveOnDone),
                SubagentTimeoutMilliseconds = OptionalInt(root, "subagentTimeoutMs", defaults.SubagentTimeoutMilliseconds),
                TestTimeoutMilliseconds = OptionalInt(root, "testTimeoutMs", defaults.TestTimeoutMilliseconds),
                FirstOutputTimeoutMsByTier = firstOutputTiers,
                FirstOutputTimeoutMs = OptionalInt(root, "firstOutputTimeoutMs", defaults.FirstOutputTimeoutMs),
                CommitProofArtifacts = OptionalBool(root, "commitProofArtifacts", defaults.CommitProofArtifacts),
                MaxPlanConcurrency = OptionalInt(root, "maxPlanConcurrency", defaults.MaxPlanConcurrency),
                InactivityTimeoutMsByTier = inactivityTiers,
                InactivityTimeoutMs = OptionalInt(root, "inactivityTimeoutMs", defaults.InactivityTimeoutMs),
                OutputSilenceTimeoutMsByTier = outputSilenceTiers,
                OutputSilenceTimeoutMs = OptionalInt(root, "outputSilenceTimeoutMs", defaults.OutputSilenceTimeoutMs),
                TestIdleGraceMilliseconds = OptionalInt(root, "testIdleGraceMs", defaults.TestIdleGraceMilliseconds),
                BootstrapFiles = OptionalStringArray(root, "bootstrapFiles", []),
                BootstrapCheckCommand = OptionalStringOrNull(root, "bootstrapCheckCmd"),
                GuardCommand = OptionalStringOrNull(root, "guardCmd"),
                FormatCommand = OptionalStringOrNull(root, "formatCmd"),
                VisualRenderCmd = OptionalStringOrNull(root, "visualRenderCmd"),
                BoostTurnsTaskIds = OptionalStringArray(root, "boostTurnsTaskIds", []),
                SkipTestsTaskIds = OptionalStringArray(root, "skipTestsTaskIds", []),
                NewGuardPatterns = OptionalStringArray(root, "newGuardPatterns", defaults.NewGuardPatterns),
                DownshiftOnEarlyImplementation = OptionalBool(root, "downshiftOnEarlyImplementation", defaults.DownshiftOnEarlyImplementation),
                RetryFlakyVerify = OptionalBool(root, "retryFlakyVerify", defaults.RetryFlakyVerify),
                SandboxExtraAllowPaths = sandboxExtraAllowPaths,
                TierModelOverrides = tierModelOverrides,
                TestPaths = OptionalStringArray(root, "testPaths", [])
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
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : fallback;
    private static bool OptionalBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;
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
