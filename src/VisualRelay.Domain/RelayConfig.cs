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
    // Per-tier first-output watchdog (ms). A swival invocation that emits zero
    // trace entries / stdout bytes within this window is killed and retried
    // (up to MaxStallRetries). The threshold varies sharply by tier:
    //   cheap    ~90 s  (healthy max ~34 s)
    //   balanced ~120 s (healthy max ~46 s)
    //   frontier ~660 s (healthy max ~412 s; Review stage, heaviest reasoning)
    // Tiers not in the map fall back to FirstOutputTimeoutMs.
    IReadOnlyDictionary<string, int> FirstOutputTimeoutMsByTier,
    // Fallback (ms) for tiers absent from FirstOutputTimeoutMsByTier.
    int FirstOutputTimeoutMs,
    // Maximum retries after a first-output stall kill before giving up.
    int MaxStallRetries,
    // Default true: the nono sandbox wrapping added by the sandbox-1 task invokes
    // swival with `--sandbox nono --nono-profile vr-guard --nono-rollback`, an
    // interface nono v0.62.0 does not accept (it prints its version and exits 1),
    // which breaks EVERY swival call. Bypassed by default until the nono integration
    // is implemented against nono's real CLI; set bypassSandbox:false to re-enable.
    bool BypassSandbox = true);
