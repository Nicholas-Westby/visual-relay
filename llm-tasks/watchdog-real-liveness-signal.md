# Give stall detection a real liveness signal — and make the stage cap inactivity-based

> **Amended 2026-06-09 (W's directive):** don't just fix the first-output watchdog —
> replace the *flat per-stage wall-clock cap* with a **sliding inactivity deadline** that
> resets every time activity is detected. `maxTurns` (already 200) is the churn bound; a
> healthy 29-min Implement (observed today on parallelize) should never die to a clock,
> and a genuinely hung stage should die after ~minutes of *silence*, not after burning
> the rest of a 40-min budget. One liveness stream, two consumers: (a) first-output
> watchdog (arm until first signal), (b) ongoing inactivity deadline (reset per signal).

## Problem (original)

`FirstOutputWatchdog.WaitAsync` (`src/VisualRelay.Core/Execution/ProcessRunners.Watchdog.cs`)
treats "first filesystem entry appears in `--trace-dir`" as the sole liveness signal
(`ProcessRunners.cs:91` calls it; `:82` tails the dir; `:178` passes `--trace-dir`). But
swival writes its first trace file **only after its first turn completes**, so heavy
first turns get false-killed while making successful proxy calls and writing repo files
(empirically 2026-06-09: stage killed 3× at per-tier budget with proxy TTFT 1.4s and 40+
successful calls). Stopgap since `41f81e8`: budgets set above the per-stage cap, i.e.
watchdog disabled; the flat cap (now 40 min via `subagentTimeoutMs`) is the only
backstop — coarse in both directions (hung stages waste the full window; healthy heavy
stages need ever-larger windows: parallelize's Implement ran 29 min healthy today).

## Goal

- A stage that is producing **any** observable activity (subprocess stdout/stderr bytes,
  new trace-dir entries, trace-file growth) is never killed by a timer.
- A stage with **no** activity for the per-tier inactivity window is killed and retried
  (existing stall-retry machinery), catching genuine stalls in minutes.
- First-turn liveness disarms on a signal that exists *during* turn 1 (process output
  byte), not after it (trace file).
- `maxTurns` remains the churn bound; optionally keep an absolute ceiling
  (`subagentTimeoutMs`, generous, possibly 0=off) as a last-resort backstop.

## Approach (suggested)

- `ProcessCapture.RunAsync` (`ProcessRunners.cs`) already captures stdout/stderr: expose
  an activity callback/`IProgress`-style pulse on every output chunk; pulse likewise from
  the existing trace-dir tailer on each new entry AND on trace-file size growth (entries
  may flush per-turn; size growth can be sub-turn).
- Replace the fixed deadline around the subagent wait with a sliding one: deadline =
  `last_pulse + inactivityTimeoutMsByTier[tier]`. Config: new
  `inactivityTimeoutMsByTier` (suggested defaults: cheap/balanced ~600000, frontier
  ~1200000 — must exceed the worst healthy single-turn silence observed for the tier),
  loaded like `firstOutputTimeoutMsByTier` in `RelayConfigLoader`. Keep
  `firstOutputTimeoutMsByTier` for the pre-first-signal window (it can return to sane
  values, e.g. 120s cheap/balanced, 660s frontier, once the signal is process-output).
- Semantics of existing `subagentTimeoutMs`: repurpose as optional absolute ceiling
  (document; consider default 0 = disabled now that inactivity + maxTurns cover the
  failure modes).
- Emit the existing stall/kill events with which signal source last pulsed, so logs show
  *why* a kill fired (`no activity for Xs; last signal: stdout@T`).
- Regression tests (extend `SwivalSubagentRunnerWatchdogTests.cs` harness): (1) process
  writing stdout but no trace file is NOT killed (original false-kill); (2) totally
  silent process IS killed at the inactivity window and retried; (3) process silent for
  > window then active is killed (no resurrection); (4) activity pulses extend a stage
  past the old flat cap without a kill; (5) absolute ceiling (when set) kills despite
  activity; (6) per-tier windows honored.

Related: memory `vr-stall-often-watchdog-false-kill`. Supersedes the trace-file approach
in `DONE-swival-first-output-watchdog.md`. Coordinates with `stage-contract-retry`
(same ProcessRunners surface — land sequentially, contract-retry first).
