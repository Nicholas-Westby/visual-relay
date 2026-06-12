# Inactivity watchdog let a socket-wedged, fully silent agent run 19+ minutes past its 600s window

Observed 2026-06-12 during a JobFinder drain (`drain-20260612152036.log`,
task `fix-jobs-scored-zero`, run `20260612162209`, stage s8/balanced,
swival 1.0.30 under nono):

- 16:33:54Z — `s8/balanced stage_start name=Fix` written to run.log. After the
  prompt echo + first turns (~65 KB stdout, persisted at
  `stage8-attempt1.killed-output.txt`), **nothing more ever appeared**:
  `stage8-attempt1/` trace dir stayed empty, run.log stayed silent.
- The agent's TCP connection to the litellm proxy sat in **CLOSE_WAIT**
  (server closed; `backend.sh` exports
  `LITELLM_MAX_STREAMING_DURATION_SECONDS=240`, so a stalled upstream stream
  is cut server-side at 4 min). swival's HTTP client has no read deadline and
  blocked forever on the dead socket.
- Process-tree CPU over a 20s sample: `0:06.93 → 0:06.95` (~1 ms/s — pure
  event-loop poll noise). That is far below the cpu-pulse epsilon
  (`ProcessCapture.cs:10`, `CpuPulseEpsilonMs = 50` per 4s sample,
  `SwivalSubagentRunner` `CpuPulseSampleIntervalMs = 4_000`), so **no cpu
  pulses were being emitted** — cpu-pulse is not what kept it alive.
- JobFinder's `.relay/config.json` sets no timeout overrides, so defaults
  applied: `InactivityTimeoutMs = 600_000`, by-tier first-output
  `balanced = 120_000` (`RelayConfigLoader.cs` defaults block). A kill was
  due by ~16:44Z. At ~16:53Z (19+ min of total silence) the agent was still
  running and was killed manually (TERM → exit 143). Stall-retry machinery
  then worked exactly as designed: output persisted, `stage8-attempt2`
  spawned, the run recovered.

So with every visible activity channel dead — no trace writes, no run.log
writes, no stdout, no cpu pulses above epsilon — `ActivityWatchdog`
(`ProcessRunners.Watchdog.cs`) never reached its inactivity verdict. Either
some pulse source still fired (what?), the watchdog loop itself was wedged,
or the arming path in `ProcessRunners.RunAsync.cs:57-83` mis-derived the
deadline. That is the diagnosis to nail down — the evidence above says the
window was armed (600s) and exceeded by ~2x.

## Goal

An agent that produces no trace activity, no process output, and no
above-epsilon CPU for the configured inactivity window is killed and
stall-retried at that window — never hours, never "until someone looks".
Specifically: the exact pulse pattern observed here (early output burst,
then total silence with ~1 ms/s cpu accrual and an open-but-dead socket)
must produce a kill at `inactivityTimeoutMs` ± one sample interval.

## Approach (suggested)

- First reproduce in a test: drive `ActivityWatchdog` / the RunAsync arming
  path with a simulated pulse history — pulses for the first ~2 min, then
  none — and assert the kill fires at the inactivity deadline. If it passes,
  the bug is upstream of the watchdog: audit every `Pulse(...)` call site
  (e.g. `ProcessRunners.RunAsync.cs:83` wires `onActivity` → `Pulse("trace")`)
  for sources that fire without real agent activity (stream-handle liveness,
  sampler error paths, timer recomputation resetting `_lastPulseTimestamp`,
  tier-escalation recompute at `RunAsync.cs:57-59`).
- Add a regression test for the cpu epsilon edge: cumulative-but-tiny accrual
  (1 ms/s, 4s interval) must never pulse (the per-sample baseline reset makes
  this hold today — pin it with a test so a future "accumulate deltas" change
  doesn't quietly turn poll-noise into liveness).
- Consider logging a watchdog heartbeat line (e.g. every 60s: silenceMs,
  lastPulseSource, deadline) to run.log at debug level — this incident was
  undiagnosable from artifacts alone precisely because the watchdog's own
  view of pulse history is invisible.
