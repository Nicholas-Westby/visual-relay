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

## What to fix

The robust, toolchain-agnostic mechanism is the HARD PER-ATTEMPT ABSOLUTE CEILING that
already exists — `_absoluteCeilingMs` in `ActivityWatchdog` (config
`SubagentTimeoutMilliseconds`, surfaced as `Outcome.FiredAbsoluteCeiling`). Ensure every
stage attempt sets it to a sane wall-clock bound and NEVER leaves it 0/unset, so no attempt
can run unbounded regardless of how "active" it looks. The observed 40-min flail ran
unbounded because the ceiling was effectively unset for that stage; a sane ceiling caps it
and the existing stage-retry path takes over.

**Scale the ceiling with the 10× turn boost.** A task whose id is in
`RelayConfig.BoostTurnsTaskIds` is given `MaxTurns × TurnBoostMultiplier` (10×) turns
(`RelayDriver.VerifyFix.cs:216-218`) — the user has declared it legitimately long-running.
The absolute ceiling MUST mirror that: multiply the per-attempt ceiling by
`TurnBoostMultiplier` for boosted tasks, or a boosted (correctly long-running) task will be
reaped by an unscaled ceiling, silently defeating the boost. This is latent today
(`subagentTimeoutMs` is 0, so no ceiling exists yet) but becomes load-bearing the moment
this task introduces one — wire the boost in at the same time, not as an afterthought.

Do NOT add a "no new trace events ⇒ stalled" heuristic. The trace (`trace.jsonl`) does grow
incrementally per turn (the watchdog already pulses on trace-dir growth), BUT within a single
long *legitimate* tool call — a build, a test run — there are no new trace events for minutes
while only the tool's stdout/CPU pulse. That is indistinguishable from a flailing agent
spinning in a broken background-poll loop (also stdout/CPU active, no new trace events). So
trace-event absence cannot separate "flailing" from "working hard on a slow tool", and would
false-positive on slow-but-legitimate stages. The absolute ceiling is the reliable backstop;
progress heuristics are not.

(Lower urgency: the targeted-self-verify change already removed the flail's usual trigger — a
too-slow full-suite self-verify — so this is a belt-and-suspenders bound, not a hot path.)

## Done when

- Every stage attempt runs under a non-zero absolute ceiling; an attempt that exceeds it is
  reaped and retried with no human kill, regardless of apparent activity.
- A legitimately slow-but-progressing stage UNDER the ceiling is NOT reaped (no trace-event
  heuristic that would false-positive on a long tool call).
- `./visual-relay check` green; Conventional Commit subject.

## Coordination

Lower urgency now that self-verify is targeted (the main flail trigger is gone), but the
detection gap is real. Distinct from `harness-idle-watchdog-spares-test-teardown` (that
one is about NOT reaping a passing teardown; this is about DOES reap a stuck agent).
