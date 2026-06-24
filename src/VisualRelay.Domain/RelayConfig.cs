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
    // Optional whole-project formatter run unconditionally before each guard
    // check. When set, the harness auto-formats the working tree so format-only
    // guard failures never trigger a Fix-verify loop. Absent (null) → no-op;
    // the existing guard behavior is unchanged. Takes no filename arguments —
    // the formatter must accept whole-project invocation (e.g. "dotnet format
    // VisualRelay.slnx", "prettier --write .", "gofmt -w .", "cargo fmt").
    // Because formatCmd only reformats the task's own changed files (when the
    // upstream repo is correctly formatted), the formatted output lands in the
    // manifest and is automatically included in the commit.
    string? FormatCommand = null,
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
    // When true (default), if the agent front-loads implementation into an earlier
    // stage (manifest impl files already differ from HEAD before Implement runs),
    // the redundant Implement narration stage runs on the cheapest tier with a
    // "confirm/amend only" prompt instead of full freight. Set false to always run
    // every stage on its declared tier. No effect on non-git roots.
    bool DownshiftOnEarlyImplementation = true,
    bool RetryFlakyVerify = true,
    // Per-repo escape hatch for exotic toolchain cache paths the vr-guard profile
    // baseline does not cover.  Each entry is appended as `-a <path>` to BOTH the
    // Swival nono run invocation and the verification nono run invocation, so the
    // effective allowlist is the UNION of the profile grants and these extras.
    // Entries are validated during config load: `..` (path traversal) is rejected
    // with a load error; `~` and `$HOME` are expanded; and each normalized absolute
    // path must resolve under $HOME or under the workspace root — paths pointing at
    // /etc, /System, other users' homes, or credential trees are rejected so the
    // escape hatch cannot re-open the destructive surface the deny groups protect.
    // Default null/empty (no extra grants).  The field is additive-only (it never
    // removes a profile grant) and grants read+write.
    IReadOnlyList<string>? SandboxExtraAllowPaths = null)
{
    // Glob patterns (relative to targetRoot) that identify guard/gate scripts.
    // When a manifest entry matches any pattern, the harness executes it once
    // unsandboxed after Fix (stage 8), before accepting Verify.  A non-zero exit
    // feeds output into the Fix-verify loop instead of letting an unverified guard
    // through.  Default: ["tools/guards/**/*.sh"] — repos that store guards
    // elsewhere should override.  Set to [] to disable.
    public IReadOnlyList<string> NewGuardPatterns { get; init; } = ["tools/guards/**/*.sh"];

    // Grace window (ms) for the sandboxed test-run path's idle-reap watchdog
    // (SandboxedTestRunner). Once the wrapped test process tree goes
    // output-silent AND CPU-idle for this long, the runner reaps the sandbox
    // wrapper and reports the inner command's real red/green result (the
    // "exited with code N" marker in the wrapper's output) instead of riding
    // TestTimeoutMilliseconds — the wrapper (nono) supervises descendants that
    // can outlive the finished tests, so it may never exit on its own. A
    // still-BUSY tree (CPU active) is never reaped; a genuine busy hang rides
    // the hard cap. Default 60_000 (60 s): far below the cap yet above any
    // legitimate output-silent + CPU-idle gap (e.g. a network wait) in a real
    // run, so a finished-then-idle tree is reaped but a working one is not.
    public int TestIdleGraceMilliseconds { get; init; } = 60_000;
}
