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
    int SubagentTimeoutMilliseconds,
    int TestTimeoutMilliseconds,
    // Default true: the nono sandbox wrapping added by the sandbox-1 task invokes
    // swival with `--sandbox nono --nono-profile vr-guard --nono-rollback`, an
    // interface nono v0.62.0 does not accept (it prints its version and exits 1),
    // which breaks EVERY swival call. Bypassed by default until the nono integration
    // is implemented against nono's real CLI; set bypassSandbox:false to re-enable.
    bool BypassSandbox = true);
