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
    bool BypassSandbox = false,
    // Maximum concurrent planning tasks during Phase 1 (parallel planning).
    // Planning stages (1–4) are read-only and run in isolated git worktrees,
    // so they are safe to overlap. Stages 5–11 always run serially.
    int MaxPlanConcurrency = 10,
    // Glob patterns for environment-bootstrap files. Null (default) means the
    // driver uses built-in defaults: flake.nix, flake.lock, *.nix, Brewfile,
    // Dockerfile*, .tool-versions, rust-toolchain*. Set to a non-empty list to
    // override. Set to [""] to disable built-in detection entirely.
    IReadOnlyList<string>? BootstrapFiles = null,
    // Smoke command that proves the bootstrap still works from a fresh
    // evaluation. Null (default) means auto-detect: nix repos (any .nix file
    // in the manifest) get "nix develop --command true"; other repos skip.
    string? BootstrapCheckCommand = null);
