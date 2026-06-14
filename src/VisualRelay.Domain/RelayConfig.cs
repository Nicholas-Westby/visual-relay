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
    // Optional absolute wall-clock ceiling per stage invocation (ms).
    // 0 = disabled (inactivity deadline + maxTurns cover failure modes).
    // When > 0, a stage is killed after this many ms regardless of activity.
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
    // Maximum corrective retries when a stage completes (exit 0) but the output
    // lacks a parseable fenced JSON contract block (or the block has wrong shape).
    // 0 preserves today's fail-fast; defaults to 1.
    int MaxContractRetries = 1,
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
    string? BootstrapCheckCommand = null,
    // Command that runs repo policy guards (file-size, format, etc.) alongside
    // the test command in the stage-9 gate. Absent → skipped with zero overhead.
    string? GuardCommand = null,
    // Per-tier inactivity timeout (ms). A stage with no liveness pulse
    // (stdout/stderr bytes, trace-dir entry, or trace-file growth) within
    // this window is killed and retried (up to MaxStallRetries).
    // Tiers not in the map fall back to InactivityTimeoutMs.
    // Suggested: cheap/balanced ~600_000 (10 min), frontier ~1_200_000 (20 min).
    IReadOnlyDictionary<string, int>? InactivityTimeoutMsByTier = null,
    // Fallback inactivity timeout (ms) for tiers absent from InactivityTimeoutMsByTier.
    // Default 600_000 (10 min).
    int InactivityTimeoutMs = 600_000,
    // When true (default), the four proof files under .relay/<taskId>/
    // (ledger.md, <taskId>.seals, manifest.txt, status.json) are force-added
    // to each relay commit so the run is verifiable.  When false, the proof
    // files are still written to disk (for local resume / re-added-task
    // detection) but are omitted from the commit.  Task retirement
    // (DONE- / archive) records are always committed regardless of this flag.
    bool CommitProofArtifacts = true,
    // Task ids whose per-stage turn budget is multiplied by 10 (for unusually
    // large tasks).
    IReadOnlyList<string>? BoostTurnsTaskIds = null,
    // When true, stage 7 (Review) runs on the `balanced` tier first; a second
    // frontier-tier Review runs only when the balanced verdict is non-pass,
    // issues are found, or a diff-complexity heuristic trips.  Default true.
    bool ReviewEscalationEnabled = true,
    // Heuristic thresholds for auto-escalation after a passing balanced Review.
    // ReviewEscalationManifestFileThreshold: escalate if manifest has more than
    // this many files. 0 = disabled.  Default 10.
    int ReviewEscalationManifestFileThreshold = 10,
    // ReviewEscalationManifestLineThreshold: escalate if total lines across
    // manifest files exceeds this. 0 = disabled.  Default 500.
    int ReviewEscalationManifestLineThreshold = 500);
