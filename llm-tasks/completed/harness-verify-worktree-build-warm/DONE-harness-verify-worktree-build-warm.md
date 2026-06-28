# Harness: warm the verify's worktree build so the gate isn't a cold full rebuild

## Problem

Verify (stage 9) and Fix-verify (stage 10) run `testCmd` in a **fresh git worktree**. A new
worktree has empty `bin/obj`, so `dotnet test` does a **cold full build** of the whole
solution (~6 min observed) before running the suite (~4 min) — ~10 min wall-clock per
verify, and again per Fix-verify. This dominates verify latency and is variable (cold vs
warm machine state). It already caused a flag: a cold run tipped past the old 10-min
`testTimeoutMs` and timed out. **Mitigated** by bumping `testTimeoutMs` to 1200000 (20 min)
in commit `04a2c20` — a band-aid that makes the gate *tolerate* the slow build rather than
fixing it.

## What to do
Make the worktree build incremental/warm so the verify spends its time running tests, not
rebuilding from scratch. Measure first, then evaluate:
- Seed the worktree's `bin/` + `obj/` from the main checkout's already-warm build before
  running `testCmd` (copy or hardlink) so `dotnet` does an incremental build. Confirm this
  is sound across a git worktree (paths/timestamps) and does NOT mask a needed rebuild of
  the agent's changes.
- Or use the MSBuild build server (`DOTNET_CLI_USE_MSBUILD_SERVER`) — but reconcile with
  `MSBUILDDISABLENODEREUSE=1` which `SandboxEnv` sets to avoid the orphaned-worker
  teardown-137 (`ProcessRunners.SandboxEnv.cs`).
- Or split the steps: `dotnet build` once (untimed, outside the blame-hang window) then
  `dotnet test --no-build` for the timed gate.

## Done when
- A typical verify's build phase is materially faster (target: incremental, not a full cold
  rebuild) with no correctness loss (the agent's changes are still compiled and tested).
- `testTimeoutMs` can be reconsidered downward once the build is fast (the user's original
  10-min value was reasonable for run-only).
- General: must not assume VR's own project layout — works for any target solution.

## Coordination
Lower priority than `harness-verify-survives-sandbox-self-tests` (that one is a correctness
blocker; this is latency). Relevant commit: `04a2c20` (the timeout band-aid).
