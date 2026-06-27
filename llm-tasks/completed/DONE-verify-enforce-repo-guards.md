# Sealed commits can violate the repo's own policy gate (Verify never runs the guards)

The pipeline's stage-9 gate runs `testCmd` (and, since `2845d28`, the bootstrap check
when bootstrap files change) — but never the repo's policy guards. Result observed
2026-06-10: fifteen 300-line-guard violations accumulated across a single day of green
sealed commits, and `./visual-relay check` (the repo's documented "full gate before
work is done", AGENTS.md) fails at its first step while every pipeline run reported
success. Any repo guard (file size, banned APIs beyond compile-time, format
verification, license headers, …) has the same blind spot: the pipeline optimizes for
"tests pass", not "repo policy holds".

## Goal

A task cannot seal a commit that makes the repo's policy gate worse: the stage-9 gate
runs a configurable guard command alongside the tests, red guard output enters the
existing fix-verify loop like failing tests, and — mirroring `baselineVerify` — only
violations NEW relative to the pre-run baseline block the commit (pre-existing debt
doesn't paralyze unrelated tasks). Zero extra cost for repos with no guard configured.

## Approach (suggested)

- Config: `guardCmd` in `.relay/config.json` (init may auto-detect e.g.
  `tools/guards/*.sh` or a `check`-style script; absent → skip). For this repo:
  the guards portion of `./visual-relay check` (file-size + format verification),
  NOT the full check (build/tests/screenshots already covered elsewhere).
- Driver: run `guardCmd` at the stage-9 gate with the test run; baseline its output at
  run start (like the test baseline) and diff — new violation lines → red → fix-verify
  loop with the guard output fed in; pre-existing-only → green with a ledger note.
- Time-box like the test runner; guard output goes verbatim into flag reasons.
- Tests (driver-level, faked runners): (a) guard red with new violations → fix-verify
  entered with output; (b) pre-existing violations only → commit proceeds, noted;
  (c) no guardCmd → no invocation; (d) guard fixed in fix-verify → seals.
