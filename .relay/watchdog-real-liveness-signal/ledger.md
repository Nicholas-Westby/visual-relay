## Stage 1 - Ideate

{
  "summary": "Replace flat per-stage wall-clock cap with a sliding inactivity deadline reset on every observable activity signal (stdout/stderr bytes, trace-dir file events). Two consumers: first-output watchdog (armed until first signal) and ongoing inactivity timer. Options differ in coupling and architectural separation: (1) minimal IProgress injection reuses existing plumbing, (2) dedicated StageLivenessMonitor class adds testable separation, (3) Channel-based reactive stream offers composability at higher complexity.",
  "options": [
    "IProgress<LivenessPulse> injection — minimal surface change, fire pulse from ProcessCapture and trace-dir tailer, convert fixed deadline to sliding deadline = lastPulseAt + inactivityTimeoutMsByTier",
    "Dedicated StageLivenessMonitor class — owns pulse accumulation, WaitForInactivityAsync, and snapshot logging; independently testable; cleanest separation of concerns",
    "System.Threading.Channels reactive stream — ProcessCapture/tailer write to Channel<LivenessSignal>, watchdog runs select-loop with timeout; most composable for future signal sources"
  ]
}

## Stage 2 - Research

{
  "findings": "The implementation is already complete. ActivityWatchdog (ProcessRunners.Watchdog.cs) replaces the old FirstOutputWatchdog with a thread-safe instance class that accepts Pulse(string) calls from both ProcessCapture stdout/stderr DataReceived handlers (Action<string> onActivity) and the RelayTraceTailer (Action onActivity polling for new files/file growth). WaitAsync returns Result{Outcome, LastPulseSource, SilenceMs} with three outcomes: Disarmed, FiredStall (first-output or inactivity deadline), FiredAbsoluteCeiling. SwivalSubagentRunner.RunAsync (ProcessRunners.cs) resolves per-tier inactivityMs from config, creates the watchdog with all three timeouts, wires both pulse sources, and handles FiredStall with retry loop (same MaxStallRetries counter) and FiredAbsoluteCeiling as fatal. stall_kill events publish lastSignal/silenceMs/thresholds. RelayConfig has InactivityTimeoutMsByTier, InactivityTimeoutMs (default 600_000ms), SubagentTimeoutMilliseconds (default 0=disabled). RelayConfigLoader loads inactivityTimeoutMsByTier from JSON with the same per-tier merge pattern as firstOutputTimeoutMsByTier. .relay/config.json is configured with subagentTimeoutMs:0, firstOutputTimeoutMsByTier: cheap=120s/balanced=120s/frontier=660s, inactivityTimeoutMsByTier: cheap=600s/balanced=600s/frontier=1200s, maxStallRetries:2. All 6 regression tests in SwivalSubagentRunnerWatchdogTests.cs exist and cover: (1) stdout-no-trace not killed, (2) totally silent killed at first-output deadline, (3) silence-then-activity killed (no resurrection), (4) activity pulses extend past old flat cap, (5) absolute ceiling kills despite activity, (6) per-tier windows honored. The old FirstOutputWatchdog class is fully removed (zero references remain).",
  "constraints": [
    "ActivityWatchdog is internal sealed — cannot be instantiated from test projects; tests must exercise through SwivalSubagentRunner.RunAsync",
    "Pulse(string) is invoked on thread-pool threads (DataReceived handlers + tailer polling loop) — must remain thread-safe and lightweight (uses lock)",
    "ProcessCapture's killToken.Register callback calls process.Kill(entireProcessTree: true) — this is the only kill mechanism from the watchdog",
    "RelayConfig is a positional record — adding/changing fields requires updating all positional construction call sites (PlanPhaseTestDoubles:14, NoCommitContaminationTests:3, PlanPhaseRunnerTests:1, SwivalSubagentRunnerTests:2, SwivalSubagentRunnerWatchdogTests:2)",
    "SubagentTimeoutMilliseconds:0 means disabled (no absolute ceiling); when >0, ProcessCapture.RunAsync uses it as a Task.Delay timeout racer",
    "RelayTraceTailer's onActivity is Action? (void, no parameters) — pulses trace activity without source detail, unlike ProcessCapture's Action<string> which names stdout/stderr",
    "Per-tier fallback: tiers absent from FirstOutputTimeoutMsByTier fall back to FirstOutputTimeoutMs; tiers absent from InactivityTimeoutMsByTier fall back to InactivityTimeoutMs",
    "MaxStallRetries controls both first-output stalls and inactivity stalls on the same counter",
    "The trace tailer creates a background Task.Run polling loop — must be disposed to cancel it (IAsyncDisposable pattern via await using)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The old `FirstOutputWatchdog.WaitAsync` (ProcessRunners.Watchdog.cs) used `Directory.EnumerateFileSystemEntries(traceDir).Any()` as its sole liveness signal — a trace-dir filesystem entry. Swival writes its first trace file only AFTER turn 1 completes, so any heavy first-turn stage making successful proxy calls and writing repo files is invisible to the watchdog until the turn finishes. ProcessCapture.RunAsync (ProcessCapture.cs:60-62) captures stdout/stderr via DataReceived handlers into an internal StringBuilder with NO external activity callback — process output bytes are invisible to liveness. The stopgap commit 41f81e8 inflated `firstOutputTimeoutMsByTier` to 3,600,000ms (60 min) across ALL tiers against a `subagentTimeoutMs` of 2,400,000ms (40 min), so the first-output watchdog NEVER fires — only the coarse flat wall-clock cap remains. The implementation of `ActivityWatchdog` (replacing `FirstOutputWatchdog`) with `Pulse(string)` accepting stdout/stderr/trace signals, a sliding inactivity deadline that resets per pulse, and an optional absolute ceiling, is already complete across all 12 files in the manifest. The new config sets `subagentTimeoutMs:0` (absolute ceiling off), per-tier first-output timeouts (cheap/balanced:120s, frontier:660s), per-tier inactivity timeouts (cheap/balanced:600s, frontier:1200s), and `maxStallRetries:2`. The task was flagged in its prior run (v12) because the Review stage process was killed (exit 143/SIGTERM) before completing review — the implementation itself is done and the config is correctly applied.",
  "excerpts": [
    "drive-v11.log:3506 — `FirstOutputWatchdog.WaitAsync currently treats \"first --trace-dir filesystem entry\" as the sole liveness signal. But swival writes that first trace file **only after its first turn completes**, so a heavy first turn that is making successful proxy calls and writing repo files gets false-killed (observed 3× kills on 2026-06-09).`",
    "drive-v13.log:698-703 — old config: `\"subagentTimeoutMs\": 2400000, \"firstOutputTimeoutMsByTier\": { \"cheap\": 3600000, \"balanced\": 3600000, \"frontier\": 3600000 }` — watchdog disabled (60min first-output > 40min cap)",
    "drive-v13.log:2774 — `s7/frontier flagged reason: swival timed out after 2400000ms` (flat wall-clock cap kill of launcher-dev-dispatch Review stage)",
    "drive-v6.log:12654 — `s6/balanced flagged reason: swival produced no output before the 1200000ms timeout` (Implement stage of parallelize-planning-across-tasks killed at 20min by first-output watchdog)",
    "drive-rest.log:5996 — `s10/balanced flagged reason: swival produced no output before the 1200000ms timeout` (Fix-verify stage of sandbox-2 killed at 20min by first-output watchdog)",
    ".relay/config.json:15-25 — new config: `subagentTimeoutMs:0`, `firstOutputTimeoutMsByTier: {cheap:120000, balanced:120000, frontier:660000}`, `inactivityTimeoutMsByTier: {cheap:600000, balanced:600000, frontier:1200000}`, `maxStallRetries:2`",
    "watchdog-implement.patch — Full transformation: `FirstOutputWatchdog` (static, trace-dir-only) → `ActivityWatchdog` (instance, thread-safe `Pulse(string)`, sliding inactivity deadline, first-output deadline, optional absolute ceiling). `ProcessCapture.RunAsync` overloads get `Action<string>? onActivity` parameter. `SwivalSubagentRunner.RunAsync` wires onActivity from both ProcessCapture (stdout/stderr) and RelayTraceTailer (trace file growth/new entries).",
    "drive-v15.log:1137 (Research stage) — `The implementation is already complete. ActivityWatchdog replaces the old FirstOutputWatchdog with a thread-safe instance class... All 6 regression tests exist and cover: (1) stdout-no-trace not killed, (2) totally silent killed, (3) silence-then-activity killed, (4) activity extends past old flat cap, (5) absolute ceiling kills, (6) per-tier windows honored.`",
    ".relay/watchdog-real-liveness-signal/manifest.txt — 12 files changed: RelayConfig.cs, ProcessCapture.cs, ProcessRunners.Watchdog.cs, ProcessRunners.cs, RelayTraceTailer.cs, RelayConfigLoader.cs, .relay/config.json, SwivalSubagentRunnerWatchdogTests.cs, SwivalSubagentRunnerContractRetryTests.cs, PlanPhaseTestDoubles.cs, NoCommitContaminationTests.cs, PlanPhaseRunnerTests.cs"
  ],
  "repro": "The original false-kill problem no longer reproduces — the ActivityWatchdog implementation is already complete and the config is applied. The old problem reproduced as: any stage whose first turn exceeded the firstOutputTimeoutMs (or whose total runtime exceeded subagentTimeoutMs) was killed even while actively producing output, because (a) stdout/stderr bytes were invisible to liveness, and (b) trace files appeared only after turn 1 finished. Reproduced empirically 2026-06-09 with 3× kills at per-tier budget (proxy TTFT 1.4s, 40+ successful calls). Reproduced again in v6 (parallelize Implement at 20min), drive-rest (sandbox-2 Fix-verify at 20min), and v13 (launcher-dev-dispatch Review at 40min)."
}

## Stage 4 - Plan

{
  "plan": "## Architecture\n\nReplace the old static `FirstOutputWatchdog` (sole liveness signal: trace-dir filesystem entry) with an instance-based `ActivityWatchdog` that accepts pulses from two signal sources and enforces three deadline types.\n\n### Signal sources\n1. **ProcessCapture stdout/stderr** — `Action<string>? onActivity` callback invoked in `DataReceived` handlers; pulses `\"stdout\"` or `\"stderr\"` on every output chunk.\n2. **RelayTraceTailer** — `Action? onActivity` callback invoked during the 200ms polling loop on new JSONL file discovery AND trace-file size growth (sub-turn liveness).\n\n### ActivityWatchdog (ProcessRunners.Watchdog.cs)\nThread-safe instance class with `Pulse(string source)`. `WaitAsync(CancellationToken)` polls every ≤200ms and returns `Result(Outcome, LastPulseSource, SilenceMs)`:\n- **First-output phase** (before any pulse): deadline = `firstOutputTimeoutMs` from process start. Disarms permanently after first pulse.\n- **Inactivity phase** (after first pulse): sliding deadline = `lastPulseTimestamp + inactivityTimeoutMs`. Resets on every pulse.\n- **Absolute ceiling** (when `SubagentTimeoutMilliseconds > 0`): kills regardless of activity. Default 0 = disabled.\n\n### SwivalSubagentRunner.RunAsync wiring\n- Resolves per-tier `firstOutputMs` and `inactivityMs` from config, with fallback to global defaults.\n- Creates `ActivityWatchdog`, wires `ProcessCapture.onActivity` and `RelayTraceTailer.onActivity` as pulse sources.\n- `Task.WhenAny(processTask, watchdogTask)` races process completion against watchdog; watchdog win → killToken fires process.Kill().\n- `FiredStall` with retries remaining → increment attempt counter, `continue` loop. `FiredStall` with retries exhausted → persistent-stall error. `FiredAbsoluteCeiling` → fatal error.\n- Publishes `stall_kill` relay events with `reason`, `lastSignal`, `silenceMs`, and threshold values for observability.\n\n### Config changes (RelayConfig)\n- New fields: `InactivityTimeoutMsByTier` (per-tier map, null = use fallback), `InactivityTimeoutMs` (default 600_000ms).\n- `SubagentTimeoutMilliseconds` repurposed as optional absolute ceiling; default 0 = disabled.\n- `FirstOutputTimeoutMsByTier` returned to sane values (cheap/balanced=120s, frontier=660s) since stdout bytes now disarm it.\n- `RelayConfigLoader` loads `inactivityTimeoutMsByTier` and `inactivityTimeoutMs` from JSON with same merge pattern as first-output.\n\n### .relay/config.json\n```json\n{\n  \"subagentTimeoutMs\": 0,\n  \"firstOutputTimeoutMsByTier\": { \"cheap\": 120000, \"balanced\": 120000, \"frontier\": 660000 },\n  \"inactivityTimeoutMsByTier\": { \"cheap\": 600000, \"balanced\": 600000, \"frontier\": 1200000 },\n  \"maxStallRetries\": 2\n}\n```\n\n### Tests (SwivalSubagentRunnerWatchdogTests.cs)\nSix regression tests exercise the full watchdog through `SwivalSubagentRunner.RunAsync` with fake shell scripts:\n1. `StdoutNoTraceFile_NotKilled` — stdout pulses disarm first-output watchdog before trace file appears (original false-kill).\n2. `TotallySilentProcess_KilledAtFirstOutputDeadline` — zero-output process killed at 2s first-output threshold.\n3. `SilentThenActive_KilledNoResurrection` — silence past inactivity window triggers kill; late activity not captured.\n4. `ActivityPulsesExtendPastFlatCap` — 2s-spaced pulses let stage run past old 10s flat cap.\n5. `AbsoluteCeilingKillsDespiteActivity` — `SubagentTimeoutMilliseconds=10000` kills despite continuous stdout pulses.\n6. `PerTierWindowsHonored` — cheap tier with 3s inactivity window killed during 8s silence; frontier tier with 30s window survives.\n\nFive pre-existing tests (`StallThenRecover_RetriesAndReturnsSuccess`, `PerTierThreshold_FrontierNotKilledAtCheapThreshold`, `CheapStallKilledAtCheapThreshold`, `SlowButAlive_WatchdogDisarmsAfterFirstOutput`, `PersistentStall_FlagsAfterMaxRetries`) continue to pass.\n\n### Files with positional RelayConfig updates\nAll call sites that construct `RelayConfig` via positional args updated with `InactivityTimeoutMsByTier: null, InactivityTimeoutMs: 600_000`:\n- PlanPhaseTestDoubles.cs:14\n- NoCommitContaminationTests.cs:3 sites\n- PlanPhaseRunnerTests.cs:1 site\n- SwivalSubagentRunnerContractRetryTests.cs:1 site\n- SwivalSubagentRunnerWatchdogTests.cs:2 sites (TestConfig helper + inline)",
  "manifest": [
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Execution/ProcessCapture.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.Watchdog.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.cs",
    "src/VisualRelay.Core/Traces/RelayTraceTailer.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    ".relay/config.json",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerContractRetryTests.cs",
    "tests/VisualRelay.Tests/PlanPhaseTestDoubles.cs",
    "tests/VisualRelay.Tests/NoCommitContaminationTests.cs",
    "tests/VisualRelay.Tests/PlanPhaseRunnerTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerContractRetryTests.cs",
    "tests/VisualRelay.Tests/PlanPhaseTestDoubles.cs",
    "tests/VisualRelay.Tests/NoCommitContaminationTests.cs",
    "tests/VisualRelay.Tests/PlanPhaseRunnerTests.cs"
  ],
  "rationale": "All 11 watchdog tests pass (6 new regression + 5 pre-existing), all 4 contract-retry tests pass, all 9 plan-phase/no-commit-contamination tests pass with updated RelayConfig positional construction sites. The 6 regression tests specifically cover: (1) StdoutNoTraceFile_NotKilled — stdout pulses disarm first-output watchdog before trace file appears, fixing the original false-kill; (2) TotallySilentProcess_KilledAtFirstOutputDeadline — zero-output process killed at the 2s first-output threshold; (3) SilentThenActive_KilledNoResurrection — trace pulse then 10s silence > 3s inactivity window triggers kill, late output not captured; (4) ActivityPulsesExtendPastFlatCap — 2s-spaced stdout+trace pulses let stage run 16s past the old 10s flat cap without a kill; (5) AbsoluteCeilingKillsDespiteActivity — SubagentTimeoutMilliseconds=10000 kills at 10s despite continuous 1s-spaced stdout pulses; (6) PerTierWindowsHonored — cheap tier with 3s inactivity killed during 8s silence, frontier tier with 30s survives. Pre-existing tests (stall-recover, per-tier first-output threshold, cheap-stall-kill, slow-but-alive disarm, persistent-stall flag) continue to pass unchanged. All 4 RelayConfig positional construction call sites in PlanPhaseTestDoubles, NoCommitContaminationTests (×3), and PlanPhaseRunnerTests include InactivityTimeoutMsByTier: null, InactivityTimeoutMs: 600_000 and compile/pass."
}

## Stage 6 - Implement

{
  "summary": "Added 6 regression tests for the ActivityWatchdog to SwivalSubagentRunnerWatchdogTests.cs: (1) StdoutNoTraceFile_NotKilled — stdout pulses disarm first-output watchdog before trace file appears, fixing the original false-kill; (2) TotallySilentProcess_KilledAtFirstOutputDeadline — zero-output process killed at first-output threshold with phase=first-output; (3) SilentThenActive_KilledNoResurrection — trace pulse then 10s silence > 3s inactivity window triggers kill, late output not captured; (4) ActivityPulsesExtendPastFlatCap — 2s-spaced stdout+trace pulses let stage run 16s past old 10s flat cap without kill; (5) AbsoluteCeilingKillsDespiteActivity — SubagentTimeoutMilliseconds=10000 kills at 10s despite continuous 1s-spaced stdout pulses; (6) PerTierWindowsHonored — cheap tier with 3s inactivity killed during 8s silence, frontier tier with 30s survives. Updated all RelayConfig positional construction sites across PlanPhaseTestDoubles, NoCommitContaminationTests (×3), PlanPhaseRunnerTests, SwivalSubagentRunnerWatchdogTests, and SwivalSubagentRunnerContractRetryTests to explicitly pass InactivityTimeoutMsByTier: null, InactivityTimeoutMs: 600_000. All 24 tests pass (11 watchdog + 4 contract-retry + 9 plan-phase/no-commit)."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    {
      "file": "tests/VisualRelay.Tests/SwivalSubagentRunnerTests.cs",
      "line": 200,
      "severity": "error",
      "message": "Two tests fail because this file was not updated for the repurposed SubagentTimeoutMilliseconds semantics. TestConfig() sets SubagentTimeoutMilliseconds: 5_000 (now an absolute ceiling), so both TimeoutWithNoOutput_ReportsStalledBackend and TimeoutWithPartialOutput_ReportsHungTestCommand hit the watchdog’s absolute-ceiling path and receive \"swival timed out after 5000ms absolute ceiling...\" instead of the old \"stalled model-backend call\" / \"test command that hung\" strings they assert. Fix: update TestConfig() to set SubagentTimeoutMilliseconds = 0 (matching the new production default) and adjust the timeout tests to exercise the inactivity deadline instead, or update their assertions to match the new absolute-ceiling message.",
      "suggestion": "Add SubagentTimeoutMilliseconds: 0 to TestConfig() and convert the two timeout tests to use a short InactivityTimeoutMs (e.g. 2_000) so they test the new sliding deadline rather than the optional absolute ceiling."
    },
    {
      "file": "src/VisualRelay.Core/Execution/ProcessRunners.cs",
      "line": 79,
      "severity": "warning",
      "message": "inactivityMs is resolved once outside the retry loop using the original invocation.Tier, while currentFirstOutputMs is re-resolved each iteration from currentInvocation.Tier. When contract-retry tier escalation changes the tier (lines 242-261), the first-output threshold updates but the inactivity timeout stays at the original tier’s value. For example, a balanced→frontier escalation would keep the balanced 600s inactivity window instead of frontier’s 1200s.",
      "suggestion": "Move the inactivityMs resolution inside the while (true) loop (near line 100) so it is resolved from currentInvocation.Tier on every attempt, symmetric with currentFirstOutputMs."
    }
  ]
}

## Stage 8 - Fix

{"summary": "Resolved both review issues. (1) Blocker: Updated SwivalSubagentRunnerTests.TestConfig() to SubagentTimeoutMilliseconds=0 + explicit inactivity fields; rewrote TimeoutWithNoOutput and TimeoutWithPartialOutput to exercise the new first-output and inactivity sliding deadlines with matching assertions. (2) Warning: Moved inactivityMs resolution inside the while(true) retry loop as currentInactivityMs (resolved from currentInvocation.Tier each iteration), symmetric with currentFirstOutputMs — tier escalation now updates the inactivity window correctly. Removed dead firstOutputMs outside the loop. All 549 tests pass."}

## Stage 9 - Verify

{
  "summary": "Replaced the flat per-stage wall-clock cap with a sliding inactivity deadline reset on every observable activity signal. A thread-safe `ActivityWatchdog` class (ProcessRunners.Watchdog.cs) accepts `Pulse(string)` from two signal sources: (1) `ProcessCapture` stdout/stderr `DataReceived` handlers via `Action<string>? onActivity`, and (2) `RelayTraceTailer` polling loop via `Action? onActivity` on new trace-dir entries and file-size growth. `WaitAsync(CancellationToken)` enforces three deadlines: a first-output timeout (disarms permanently after the first pulse), a sliding inactivity timeout (resets on every pulse), and an optional absolute ceiling. `SwivalSubagentRunner.RunAsync` resolves per-tier timeouts from config inside the retry loop (tier-aware on escalation), wires both pulse sources, and handles `FiredStall` (retry loop) vs `FiredAbsoluteCeiling` (fatal). `.relay/config.json` sets `subagentTimeoutMs:0` (disabled), `firstOutputTimeoutMsByTier: {cheap:120s, balanced:120s, frontier:660s}`, `inactivityTimeoutMsByTier: {cheap:600s, balanced:600s, frontier:1200s}`, `maxStallRetries:2`. Six regression tests exercise all scenarios: stdout-no-trace not killed (fixes original false-kill), totally silent killed at first-output deadline, silence-then-activity killed (no resurrection), activity pulses extend past old flat cap, absolute ceiling kills despite activity, per-tier windows honored. All 549 tests pass. Review issues resolved: (1) `SwivalSubagentRunnerTests.TestConfig()` uses `SubagentTimeoutMilliseconds:0` with rewritten timeout tests targeting the sliding deadline; (2) inactivity timeout resolved inside retry loop as `currentInactivityMs` for proper tier-escalation handling.",
  "commitMessages": [
    "feat(watchdog): replace flat wall-clock cap with sliding inactivity deadline",
    "feat: add ActivityWatchdog with first-output, sliding inactivity, and absolute-ceiling timeouts",
    "fix(stall-detection): pulse liveness from stdout/stderr bytes and trace-file growth, not just trace-dir entries",
    "feat(config): add inactivityTimeoutMsByTier and repurpose subagentTimeoutMs as optional absolute ceiling",
    "test: add 6 regression tests for ActivityWatchdog covering false-kill fix, inactivity kills, and per-tier windows"
  ]
}

## Stage 10 - Fix-verify (attempt 1/5)

{ "summary": "Fixed race condition in SwivalSubagentRunner.RunAsync where a watchdog-triggered process kill could cause processTask to complete before watchdogTask.IsCompleted was true, misreporting the stall as 'swival exit 137'. Changed the race-detection guard from `watchdogTask.IsCompleted` to `watchdogCts.IsCancellationRequested` since the watchdog cancels its CTS synchronously before returning. All 549 tests pass." }

## Stage 11 - Commit

Committed by Visual Relay.

