# Harness: detect an agent that is busy but making no progress (flail), not just idle

## Problem

A stage agent (swival) can get into a state where it is NOT output-silent and NOT
CPU-idle — so the idle-reap watchdog never fires — yet it is making no real progress:
it spawned a long/looping subprocess (e.g. a self-verify it backgrounds, polls, then
kills) and spins without producing trace events or advancing the stage. Observed live: a
stage-6 agent burned ~40 minutes this way; the watchdog did not reap it and a human had
to kill it manually. (The common trigger — a too-slow full-suite self-verify — was
addressed separately by targeting self-verify at the relevant test files; this task is
about the watchdog's blind spot itself.)

## What to fix (re-grep the watchdog / activity-pulse logic in
`src/VisualRelay.Core/Execution/` — `RunWatchedAsync`, the CPU/output pulse sources)

The liveness signal currently treats any output or CPU activity as "progressing", so a
busy-but-stuck agent looks alive forever. Add a progress-based stall guard that is
toolchain-agnostic, e.g.: if no NEW trace event / stage-token / turn has been recorded
for a (generous) window despite the process being "active", treat it as a stall and
reap + fail the attempt so the existing stage-retry path takes over. Keep a hard upper
bound per attempt regardless of apparent activity.

## Done when

- A genuinely flailing agent (active CPU/output but no trace/turn progress for the window)
  is reaped and the stage retried, without a human kill.
- A legitimately slow-but-progressing stage (steady trace events) is NOT reaped.
- `./visual-relay check` green; Conventional Commit subject.

## Coordination

Lower urgency now that self-verify is targeted (the main flail trigger is gone), but the
detection gap is real. Distinct from `harness-idle-watchdog-spares-test-teardown` (that
one is about NOT reaping a passing teardown; this is about DOES reap a stuck agent).
