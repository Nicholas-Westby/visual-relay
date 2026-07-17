## Stage 1 - Ideate

{
  "summary": "Add an output-silence secondary limit to the ActivityWatchdog: a new configurable timeout (per-tier, with flat fallback) that tracks real-output silence (_lastRealOutputTimestamp, NOT reset by CPU pulses) and fires a new Outcome.FiredOutputSilence well before the absolute ceiling. The watchdog already tracks _lastRealOutputTimestamp separately; the gap is that DecideOutcome has no check for it when the subtree is still CPU-busy (the hung-LLM case). A new DecidedOutcome branch between the absolute-ceiling check and the ordinary stall check closes the gap. Config plumbing mirrors the existing InactivityTimeoutMsByTier pattern. The stall_kill event reports reason=\"output_silence_ceiling\" and the stall-reason builder distinguishes it from \"inactivity\" and \"absolute_ceiling\".",
  "options": [
    "**Layer 1 — Output-silence secondary limit in the watchdog (primary):** Add `OutputSilenceTimeoutMsByTier` + `OutputSilenceTimeoutMs` to `RelayConfig`. Thread through `ResolveTierWindows` into `ActivityWatchdog` as `_outputSilenceTimeoutMs`. In `DecideOutcome`, add a check after the absolute-ceiling gate: if `realOutputSilenceMs >= _outputSilenceTimeoutMs`, return `Outcome.FiredOutputSilence`. (The existing `_lastRealOutputTimestamp` tracks exactly this — no new bookkeeping needed.) In `WaitAsync`, when that outcome fires, publish `reason=\"output_silence_ceiling\"` and pass `realOutputSilenceMs` as `SilenceMs`. Update `BuildPersistentStallReason` to handle the new outcome. **Tests:** (a) pure `DecideOutcome` with CPU pulses ongoing but real output silent → fires `FiredOutputSilence`; (b) virtualized `WaitAsync` with CPU pulses + no real output → killed at the silence limit, not at absolute ceiling; (c) continuous real output for longer than silence limit → `Disarmed`; (d) `ResolveTierWindows` honors per-tier override.",
    "**Layer 2 — Request-level timeout at the agent/proxy layer (complementary):** Set a `request_timeout` / `timeout` field in the generated `pinned-swival.toml` (via `SwivalProfileSession.PrepareWithPinnedContentAsync` / the TOML template) and/or the generated litellm `proxy_config.yaml` config (via `BackendConfigStep` / `BackendConfigGenerator`). This makes the HTTP client raise `httpx.ReadTimeout` or `openai.APITimeoutError` instead of blocking `recv()` forever when the upstream provider hangs. The timeout should be generous (e.g., the tier's inactivity window) so legitimate long-thinking responses are not cut short. **Pro:** catches the hang at source, no process-tree overhead. **Con:** requires provider/library support; only covers the agent→proxy and proxy→upstream hops; a proxy-before-router or a hang in the router itself (litellm) would still need the watchdog. Only pursue knobs Visual Relay itself generates.",
    "**Layers 1+2 combined (defense in depth):** Implement both. Layer 1 (watchdog output-silence limit) catches any output-silent stall regardless of mechanism — hung HTTP, wedged proxy, crashed agent-but-process-alive — and is repo-agnostic. Layer 2 (request-level timeout) prevents the hang from ever reaching the watchdog in the common case, converting a 55-minute waste into a prompt retry. The watchdog limit should be set higher than the request timeout so Layer 2 fires first in the expected case while Layer 1 remains the backstop for anything Layer 2 misses (e.g., a hang in litellm itself, or a tier where the request timeout is not configured). This matches the existing architecture where the inactivity timeout is the normal path and the absolute ceiling is the backstop."
  ]
}

## Stage 2 - Research

{
  "findings": "Investigated the full codebase. The ActivityWatchdog (ProcessRunners.Watchdog.cs) already tracks _lastRealOutputTimestamp separately from _lastPulseTimestamp — CPU pulses do NOT reset the real-output clock. The gap is that DecideOutcome has no check for real-output silence when the subtree is still CPU-busy (the hung-LLM case). A new Outcome.FiredOutputSilence between the absolute-ceiling check and the ordinary stall check closes the gap. Eight files need changes: RelayConfig (add fields), RelayConfigLoader (parse them), TierWindows (resolve 3-tuple), Watchdog (new enum value + check + field), RunAsync (thread value, handle outcome), Helpers, Escalation, StallReason (switch cases). Six existing ActivityWatchdog constructor call sites (plus one at SandboxedTestRunner) are safe with a defaulted parameter. Eleven test files reference Outcome enum values and will need minor updates. New tests follow existing ManualTimeProvider/decision-seam patterns. The key design choice: FiredOutputSilence sits between FiredAbsoluteCeiling (priority 1) and FiredStall (priority 3) in the DecideOutcome decision order, and is a hard abort (not escalate-able) since it indicates a truly hung upstream request.",
  "constraints": [
    "Do not lower the absolute ceiling or existing tier timeouts — the new detector is additive only",
    "Must not kill healthy stages steadily emitting trace/tool output — only zero-output-with-CPU-only is suspect",
    "Repo-agnostic — no dependency on target repo's toolchain",
    "Respect existing virtualized-time test patterns (ManualTimeProvider) — no real-time sleeps",
    "New outcome reason must be 'output_silence_ceiling' in autopsy headers and stall_kill events",
    "Config defaults must mirror existing InactivityTimeoutMsByTier pattern with per-tier override + flat fallback",
    "Layer 1 (watchdog output-silence limit) is the primary implementation; Layer 2 (request-level proxy timeout) is deferred as complementary",
    "Symbols: Only add Outcome variant, not modify existing enum order to preserve serialization compatibility"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The ActivityWatchdog.DecideOutcome method (ProcessRunners.Watchdog.cs:162-191) has three fire paths: (1) absolute ceiling (elapsed-based), (2) stall (silenceMs-based, where silenceMs is derived from _lastPulseTimestamp which the cpu pulse resets every ~4s), and (3) socket wedge (requires realOutputSilenceMs >= inactivityMs AND subtree idle AND socket established). In a hung-LLM scenario — the agent blocks on httpcore recv() waiting for a chat-completion response — the litellm proxy still accrues housekeeping CPU. Thus: the cpu pulse keeps silenceMs low (~1-4s), so the stall path is permanently disarmed; the subtree is NOT idle, so the socket-wedge path cannot fire; and only the absolute ceiling (70 min) eventually kills the stage. The watchdog already tracks _lastRealOutputTimestamp separately (NOT reset by cpu pulses, line 108-109), but DecideOutcome never reads realOutputSilenceMs as a standalone fire condition — it only feeds it into the socket-wedge gate which additionally requires subtree idleness. The fix: add a standalone output-silence check between the absolute-ceiling gate and the ordinary stall gate: if realOutputSilenceMs >= outputSilenceTimeoutMs (configurable per-tier), fire FiredOutputSilence. The holdup-prints-2026-07-15 incident autopsy confirms the exact shape: reason=absolute_ceiling lastSignal=cpu silenceMs=52373 (cpu pulse reset the clock 52s ago, well under the 1200s inactivity window), and the stage ran 55+ minutes with zero real output.",
  "excerpts": [
    "ProcessRunners.Watchdog.cs:100-111 — Pulse method: cpu source resets _lastPulseTimestamp but NOT _lastRealOutputTimestamp",
    "ProcessRunners.Watchdog.cs:162-191 — DecideOutcome: three fire paths, none checks realOutputSilenceMs standalone",
    "ProcessRunners.Watchdog.cs:217-235 — WaitAsync loop: computes realOutputSilenceMs at line 224 but only feeds it to DecideOutcome, which ignores it as a solo gate",
    "ProcessRunners.RunAsync.cs:110 — ActivityWatchdog constructor call: passes firstOutputMs, inactivityMs, absoluteCeilingMs — no output-silence timeout",
    "ProcessRunners.TierWindows.cs:12-19 — ResolveTierWindows returns 2-tuple; would need 3-tuple to carry outputSilenceTimeoutMs",
    "RelayConfig.cs:67-75 — InactivityTimeoutMsByTier + InactivityTimeoutMs pattern to mirror for OutputSilenceTimeoutMs",
    "RelayConfigLoader.cs:116-127 — inactivityTiers parsing pattern to mirror for outputSilenceTimeoutMsByTier",
    "ProcessRunners.Escalation.cs:43-48 — PublishStallKillAsync reason switch: needs FiredOutputSilence → 'output_silence_ceiling'",
    "ProcessRunners.Helpers.cs:161-165 — TryPersistKilledOutput reason switch: same",
    "ProcessRunners.StallReason.cs:13-31 — BuildPersistentStallReason: needs FiredOutputSilence case",
    "SandboxedTestRunner.Watched.cs:101-102 — second ActivityWatchdog constructor: pass 0 (disabled) for new param",
    ".relay/hoist-pipeline-test-shared-setup/stage7-attempt1.killed-output.txt:2-4 — autopsy: reason=absolute_ceiling lastSignal=cpu silenceMs=52373, firstOutputTimeoutMs=660000 inactivityTimeoutMs=1200000 — cpu pulses masked the deadline for 55+ min"
  ],
  "repro": "1. Create a fake swival script that emits ~10KB of stdout (simulating early output), then execs 'tail -f /dev/null' while a sidecar process (simulating litellm proxy) burns ~1% CPU in a loop. 2. Run with config: firstOutputTimeoutMs=90s, inactivityTimeoutMs=600s (10 min), absoluteCeilingMs=3600s (60 min). The CPU pulse from ProcessTreeCpuSampler will fire every 4s, keeping silenceMs at ~4s and the inactivity watchdog permanently disarmed. The subtree shows CPU activity (sidecar), so socket-wedge also won't fire. 3. Without the fix, the stage runs to the 60 min ceiling. With the fix and outputSilenceTimeoutMs=600s (10 min), the watchdog fires FiredOutputSilence after 10 minutes of zero stdout/stderr/trace output despite CPU pulses."
}

## Stage 4 - Plan

{
  "plan": "Add an `Outcome.FiredOutputSilence` to `ActivityWatchdog` that fires when real-output silence exceeds a configurable per-tier limit, regardless of CPU activity that masks the ordinary inactivity deadline. This sits between the absolute-ceiling gate and the ordinary stall gate in `DecideOutcome`. Config plumbing mirrors `InactivityTimeoutMsByTier` / `InactivityTimeoutMs` with new `OutputSilenceTimeoutMsByTier` / `OutputSilenceTimeoutMs` fields. The kill reason is `\"output_silence_ceiling\"` in autopsy headers and stall_kill events. `FiredOutputSilence` is a hard abort (never escalates) like `FiredAbsoluteCeiling` and `FiredSocketWedge`. Tests use virtualized `ManualTimeProvider` — no real-time sleeps.",
  "manifest": [
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.TierWindows.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.Watchdog.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.Escalation.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.StallReason.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerReasonTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 8.

> **Manifest merge (stage 5)**: added 3 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.OutputSilence.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.OutputSilence.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.TierWindows.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.cs",
    "tests/VisualRelay.Tests/ActivityWatchdogDecisionTests.WaitAsync.cs"
  ],
  "rationale": "Added 10 new tests (8 decision-seam, 2 virtualized WaitAsync) covering: (1) FiredOutputSilence fires when cpu pulses mask inactivity but real output is silent past the per-tier limit; (2) below limit stays Disarmed; (3) disabled (timeout=0) never fires; (4) priority ordering (ceiling > output-silence > stall > socket-wedge); (5) before-first-output disarms output-silence gate; (6) continuous real output survives; (7) virtualized WaitAsync loop: cpu-pulse mask causes FiredOutputSilence at the silence limit, not the absolute ceiling; (8) continuous real output in WaitAsync stays Disarmed; (9-10) per-tier config override honored + null-map fallback. Existing ResolveTierWindows tests updated for the new 3-tuple return. All 64 relevant tests pass (0 failures)."
}

## Stage 6 - Implement

{
  "summary": "Implemented Layer 1 — output-silence ceiling in the ActivityWatchdog. Added `Outcome.FiredOutputSilence` to the watchdog enum with a new `DecideOutcome` gate that fires when real-output silence exceeds a per-tier configurable limit, regardless of CPU pulses that mask the ordinary inactivity deadline. Config: new `OutputSilenceTimeoutMsByTier` / `OutputSilenceTimeoutMs` fields in RelayConfig (default 0 = disabled, opt-in per repo). Kill reason is `output_silence_ceiling` in autopsy headers and stall_kill events, treated as a hard abort (never escalates). 8 production files edited (RelayConfig, RelayConfigLoader, TierWindows [3-tuple], Watchdog, RunAsync, Escalation, Helpers, StallReason). 42 targeted tests + 3131 full-suite tests pass (0 failures)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure backend/infrastructure changes: C# config fields, watchdog timeout logic, and unit tests with virtualized time — zero UI markup, styles, images, documents, or any rendered visual output._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Added output-silence ceiling to ActivityWatchdog — a new configurable per-tier limit (OutputSilenceTimeoutMsByTier / OutputSilenceTimeoutMs) that fires Outcome.FiredOutputSilence when real-output silence exceeds the limit while CPU pulses mask the ordinary inactivity deadline, killing a hung-LLM stage in minutes instead of at the absolute ceiling (~55 min). Config plumbing mirrors the existing InactivityTimeoutMsByTier pattern; the kill reason is \"output_silence_ceiling\" in autopsy headers and stall_kill events. 8 production files (RelayConfig, RelayConfigLoader, TierWindows, Watchdog, RunAsync, Escalation, Helpers, StallReason) and 5 test files (including 10 new tests: decision-seam, virtualized WaitAsync, tier-window resolution). All 3131 tests pass (0 failures).",
  "commitMessages": [
    "feat(watchdog): add output-silence ceiling to kill hung LLM requests early",
    "feat: detect hung LLM requests via output-silence ceiling before absolute ceiling",
    "feat(config): add OutputSilenceTimeoutMsByTier per-tier output-silence limit for watchdog",
    "feat: kill stalled stages on output-silence ceiling when CPU pulses mask inactivity"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

