# Harness: verify by the test command's exit code + stored output, not by parsing results

## Why

VR runs on many codebases with varied test output formats (jest/pytest/go/cargo/xunit/…).
Parsing test output for pass/fail counts is unreliable across all of them — the **exit code is
the only universal pass/fail signal**. I (Claude) wrongly added a TRX-counter-parsing verify
wrapper (`tools/VisualRelay.Guards/VerifyRunner.cs`, invoked via `.relay/config.json`
`testCmd = "sh tools/vr-verify.sh"`) to paper over exit-137 crashes. That 137 was NOT a real
failure — it was this repo's ~11 real-sleep tests hanging and getting killed (fixed by
[[harness-no-real-sleeps-in-tests]]). The wrapper is the wrong approach AND it swallows the test
output, so failures lost their diagnostic detail.

## What to do (do this AFTER the suite is sleep-free and exits 0 cleanly)

- Revert `.relay/config.json` `testCmd` back to the raw test command (`dotnet test …`), so VR
  uses the command's exit code directly (VR's `SandboxedTestRunner`/`InterpretWatched` already
  do exit-code-based pass/fail).
- Delete the wrapper: `tools/vr-verify.sh`, `tools/VisualRelay.Guards/VerifyRunner.cs`, and the
  `"verify"` case wiring in `tools/VisualRelay.Guards/Program.cs`.
- Confirm the full test output is persisted (VR already writes `stageN.verify-output.txt`) so the
  agent can scan it for diagnosis — that is the intended "store output, let the agent read it"
  design; no parsing on VR's side.
- Re-tighten `.relay/config.json` `testTimeoutMs` from the inflated 1_200_000 (20 min) back to
  600_000 (10 min, per the user) so a real future hang is caught, not masked.

## Done when

- Verify decides pass/fail from the raw test command's exit code; no output parsing remains.
- A red test makes verify red with the failing detail visible in `verify-output.txt`.
- A clean suite verifies green with no wrapper. `./visual-relay check` green; Conventional Commit.

## Coordination

Depends on [[harness-no-real-sleeps-in-tests]] (the suite must exit 0 cleanly first, else the raw
exit code is non-zero). Reverses commits `f5c7fae`/`7c6cc5d`/`3d848c3` and the `testCmd` change.
