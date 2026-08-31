using VisualRelay.Domain;
namespace VisualRelay.Core.Configuration;

/// <summary>
/// The built-in configuration a repository gets before it has a
/// <c>.relay/config.json</c> of its own.
/// </summary>
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
                ["frontier"] = 660_000,
                // vision was absent, so stage 8 inherited frontier's flat 660s ceiling.
                ["vision"] = 180_000
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
}
