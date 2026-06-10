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
    // Default false: nono OS-level sandboxing (Seatbelt on macOS, Landlock on
    // Linux) is a REQUIRED dependency. Swival runs under `nono run -p vr-guard`
    // by default. Set bypassSandbox:true in .relay/config.json to opt out —
    // this is the only supported no-nono path, never a silent fallback.
    bool BypassSandbox = false);
