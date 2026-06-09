# Add a per-tier first-output watchdog to the swival runner (bound + retry pre-stream stalls)

`SwivalSubagentRunner.RunAsync` (`src/VisualRelay.Core/Execution/ProcessRunners.cs`)
runs swival under a single `SubagentTimeoutMilliseconds` wall-clock (default
1,200,000 ms / 20 min). When the upstream model provider **accepts a request but
never returns the first response byte** — an intermittent pre-stream hang — swival
produces **zero** output until the 20-minute kill, then the task flags ("swival
produced no output before the … timeout").

This is the confirmed root cause of the `fix-timing-estimates` and
`careers-page-net` stage-10 stalls (the stalled swival's trace dir is empty and
LiteLLM logs no completion for it, while *other* concurrent requests return 200s
throughout — the backend is healthy; only that one upstream call hangs at byte 0).
It is **intermittent and content-independent**; the OCR tasks that completed just
didn't draw the bad call. Backend-side bounds (`request_timeout`, `stream_timeout`,
the httpx switch in `tools/backend/`) do not reliably catch the pre-first-byte
case (stalls persisted even with httpx enabled), so a transport-independent
relay-side guard is needed.

Key signal: a healthy stage emits its first trace entry quickly; a stalled one
emits **nothing**. So "no output within T" is a clean, false-positive-free stall
detector **as long as T exceeds the slowest *legitimate* first-output for that
tier** — and that varies sharply by tier (measured below), so T must be per-tier.

## Measured healthy first-output latency (N=843 stages; from stage report timelines)

| tier | p50 | p95 | p99 | max |
|---|---|---|---|---|
| cheap | 2.8s | 7.5s | 12.8s | 33.6s |
| balanced | 5.3s | 9.2s | 12.3s | 45.9s |
| frontier | 11.0s | 38.1s | 330.8s | **411.9s** |

cheap/balanced first-output is fast (≤46 s even at the max). **frontier (the Review
stage, heaviest reasoning) legitimately takes up to ~412 s before its first
output.** A single global threshold therefore can't be both tight and safe: a
naive global `2×p95 = 76 s` would kill ~4–5% of healthy frontier Reviews, while a
safe global value (~660 s) barely beats the existing 20-min timeout. **The stalls
occur on cheap/balanced** (stage-2 Research, stage-10 Fix-verify), not frontier —
so a per-tier threshold catches the real stalls in ~2 min while never false-firing
the slow-but-alive frontier Review.

## Goal

Detect a stalled swival invocation when it emits no output within a **per-tier**
window, then **kill and retry** the invocation (fresh swival process → fresh
request; the intermittent hang almost never recurs), up to a small retry cap,
before falling back to the full subagent timeout. A stall self-heals in minutes
instead of dying at 20. Once swival has produced ANY output, the watchdog disarms
and only `SubagentTimeoutMilliseconds` applies — so legitimately long-running
stages are never killed by the watchdog.

## Approach (suggested)

- Add config `FirstOutputTimeoutMsByTier` — a tier→milliseconds map — plus a scalar
  fallback `FirstOutputTimeoutMs` for tiers not in the map. Defaults (derived from
  the table; each comfortably clears that tier's healthy max):
  - `cheap`: 90000, `balanced`: 120000, `frontier`: 660000
  - fallback `FirstOutputTimeoutMs`: 660000 (safe for any unknown tier)
  Add `MaxStallRetries` (default 2). Wire through `RelayConfig` + `RelayConfigLoader`
  (mirror the existing `TierProfiles` map reader for the per-tier dict, and the
  scalar/int readers for the others; JSON keys e.g. `firstOutputTimeoutMsByTier`,
  `firstOutputTimeoutMs`, `maxStallRetries`).
- In `RunAsync`, resolve the threshold for `invocation.Tier`
  (`FirstOutputTimeoutMsByTier.GetValueOrDefault(tier, FirstOutputTimeoutMs)`).
  While swival runs, watch for first output — the trace dir is already tailed
  (`RelayTraceTailer`); use first trace-entry arrival (and/or first stdout byte) as
  the liveness signal. If no output appears within the resolved threshold, kill the
  process tree and retry the whole invocation with a fresh `stageN-attemptM` trace
  dir, up to `MaxStallRetries`. Once any output appears, stop arming the watchdog
  and let `SubagentTimeoutMilliseconds` govern the rest of the stage.
- Record each stall+retry in the ledger/status. If every attempt stalls (retries
  exhausted), return the existing "stalled model-backend call" flag — after
  ~threshold×(MaxStallRetries+1), not a single 20-min wait.

## Files

- `src/VisualRelay.Core/Execution/ProcessRunners.cs` (`RunAsync`: per-tier watchdog + retry loop)
- `src/VisualRelay.Domain/RelayConfig.cs` + `RelayConfigLoader.cs` (`FirstOutputTimeoutMsByTier`, `FirstOutputTimeoutMs`, `MaxStallRetries`)

## Tests

Use the existing subagent-runner doubles / a fake `ProcessCapture` whose output timing is scriptable.

- **stall then recover**: a `balanced` invocation produces no output past its
  120000 ms threshold → runner kills + retries; the retry produces valid output →
  invocation returns success (NOT a timeout flag).
- **per-tier threshold honored**: a `cheap` stall is killed at ~90 s; a `frontier`
  stall is NOT killed at 120 s (its threshold is 660 s) — assert the resolved
  threshold per tier.
- **slow-but-alive**: swival emits output before the threshold then runs long
  (under `SubagentTimeoutMilliseconds`) → watchdog does NOT kill it. In particular a
  frontier stage whose first output arrives at ~400 s is NOT killed (threshold 660 s).
- **persistent stall**: every attempt stalls → flags after `MaxStallRetries`, well
  before the full subagent timeout.
- config map + scalars read from config.json (defaults applied when absent).

## Notes

Per-tier is essential: cheap/balanced first-output ≤46 s (so ~90–120 s catches a
stall in ~2 min) but frontier Review legitimately reasons up to ~412 s before its
first token. This self-heals the intermittent upstream pre-stream hang that the
backend request_timeout/stream_timeout/httpx changes don't reliably catch — the
actual reason `fix-timing-estimates` and `careers-page-net` could never finish.
See [[pipeline-mocks-process-layer-blindspot]]: this touches the live exec path, so
after it lands, smoke a REAL `run-task` (the tests mock the process layer and won't
catch a runtime regression). Keep `ProcessRunners.cs` within its line guard.
