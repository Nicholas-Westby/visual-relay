## Task: Stop CPU pulses from letting a hung LLM request run to the absolute ceiling

The stage watchdog treats CPU activity as liveness. A stage whose LLM HTTP
request hangs forever produces no output but still registers periodic CPU
pulses, so the inactivity timeout never fires and the stage runs all the way to
the absolute ceiling before being killed. On the 2026-07-15 drain that wasted
~44 minutes of wall time on a review stage that had been dead-in-the-water
since its last output, and it will do the same on any repo whenever a provider
request hangs.

### Evidence (task `hoist-pipeline-test-shared-setup`, stage 7)

- Autopsy header (`.relay/hoist-pipeline-test-shared-setup/stage7-attempt1.killed-output.txt`):
  `reason: absolute_ceiling  lastSignal: cpu  silenceMs: 52373
  firstOutputTimeoutMs: 660000  inactivityTimeoutMs: 1200000` — i.e. neither
  output timeout tripped; the kill came only from the ceiling.
- The traceback shows the swival agent blocked in `httpcore …
  self._sock.recv(max_bytes)` waiting for chat-completion response headers from
  the litellm proxy (`127.0.0.1:4000`) — a hung request, not model thinking.
- 54 `watchdog_heartbeat` events with `lastPulseSource=cpu` and silence around
  1.4 s: background CPU activity (runtime housekeeping) kept resetting the
  liveness clock the whole time.
- Total: stage output stopped ~10.7 KB in; the process survived to the ~55
  minute ceiling. The run was then flagged (see companion task
  `03-carry-stall-kill-reason-into-review-pair-flags` for the reporting/retry
  side; THIS task is about detecting the hang earlier).
- Watchdog implementation: `src/VisualRelay.Core/Execution/ProcessRunners.Watchdog.cs`
  (pulse sources, ceiling at lines ~170-180); tier timeouts come from
  `.relay/config.json` (`firstOutputTimeoutMsByTier`, `inactivityTimeoutMsByTier`,
  `subagentTimeoutMs`).

### What to build

Investigate and implement layered detection so a hung request dies in minutes,
not at the ceiling. Candidate layers — diagnose stage picks the right
combination:

1. **Output-silence secondary limit in the watchdog**: CPU pulses may extend
   liveness only up to a bounded budget; beyond N minutes with zero
   output/trace bytes, treat the stage as stalled regardless of CPU. The limit
   must comfortably exceed legitimate silent periods (long tool runs pipe
   output; pure model thinking between turns is bounded by provider streaming
   keep-alives) and be configurable per tier alongside the existing
   `inactivityTimeoutMsByTier`.
2. **Request-level timeout at the agent/proxy layer Visual Relay controls**:
   the pinned swival profile (`pinned-swival.toml` generation) and/or the
   generated litellm config could carry an explicit per-request timeout so a
   hung upstream call raises instead of blocking `recv()` forever. Only pursue
   knobs Visual Relay itself generates — do not hand-edit user machines.
3. Whatever is chosen, the kill event should say what tripped (e.g.
   `output_silence_ceiling`) so autopsies distinguish it from `absolute_ceiling`.

### Constraints

- Do not lower the absolute ceiling or the existing tier timeouts; this adds a
  more specific detector, it does not tighten general budgets.
- Must not kill healthy long stages: a stage steadily emitting trace/tool
  output for 50 minutes is fine; only zero-output-with-CPU-only is suspect.
- Repo-agnostic; nothing here may depend on the target repo's toolchain.
- Respect the existing watchdog test patterns (`virtualize-watchdog-test-waits`
  landed TimeProvider-driven tests — use them; no real-time sleeps).

### Tests (red first)

- Watchdog unit test (virtualized time): process emits output, then goes
  output-silent while CPU pulses continue → killed at the output-silence limit
  with the new outcome reason, well before the absolute ceiling.
- Healthy case: continuous output pulses for longer than the silence limit →
  not killed.
- Config plumbing test: per-tier override of the new limit is honored.

### Verification

- `./visual-relay check` fully green including the new tests.
