# Harness: the C# verify runner must surface the test command's output

## Problem

`tools/VisualRelay.Guards/VerifyRunner.cs` runs `dotnet test`, **reads** its stdout/stderr
into variables (to avoid pipe deadlock), parses the TRX counters, and prints only its own
one-line `vr-verify: counters -> executed=… failed=N …` summary. It **never writes the
captured test output anywhere**. So when a verify is red, the persisted
`stageN.verify-output.txt` shows `failed=1` but NOT which test failed or why.

Consequence: the fix-verify agent (and VR's failure-reason distiller) get no real failure
detail, so the agent must **re-run the whole suite itself** to discover the failing test —
doubling verify time on every red task. (Observed live: a stage-10 agent running its own
`dotnet test --no-build` purely to find the failure the runner had already seen.)

## What to fix

Re-grep `VerifyRunner.RunAsync`. Surface the inner command's combined output so the
distiller/agent can see the real failure, while keeping the deadlock-safe drain and the
TRX-based pass/fail decision:
- Echo the captured stdout/stderr to this process's stdout/stderr (stream it, or write it
  out after draining), THEN print the `vr-verify:` summary. The driver already captures
  the runner's stdout into `verify-output.txt`, so the failing-test lines (`Failed …`,
  the `Failed!  - Failed: N …` summary) will be present again.
- Keep the exit-code logic (0 when TRX shows zero failures despite a teardown crash;
  non-zero / propagate when the TRX shows real failures or is absent).

## Done when

- A red verify's `verify-output.txt` contains the failing test name(s) and summary, not
  just the one-line counters — so fix-verify can diagnose without re-running the suite.
- A green run still distills to empty; the 137-teardown case still reports success.
- A unit test for `VerifyRunner` covers: real failure → non-zero + output surfaced;
  green-with-teardown-137 → zero. `./visual-relay check` green; Conventional Commit subject.

## Coordination

The runner was introduced by `refactor: run verify via a csharp runner …`. This adds the
missing output passthrough. Independent of the watchdog teardown fix.
