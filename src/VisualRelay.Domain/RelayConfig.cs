namespace VisualRelay.Domain;

public sealed record RelayConfig(
    string TasksDir,
    string TestCommand,
    string TestFileCommand,
    IReadOnlyList<string> LogSources,
    IReadOnlyDictionary<string, string> TierProfiles,
    int MaxVerifyLoops,
    int MaxStageFailures,
    int MaxTurns,
    bool BaselineVerify,
    bool ArchiveOnDone,
    int SubagentTimeoutMilliseconds);
