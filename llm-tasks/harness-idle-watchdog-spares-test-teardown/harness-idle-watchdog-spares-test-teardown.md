# Harness: idle-reap watchdog must not kill a passing test process during teardown

## Problem

The stage-9 verify routinely ends with the test process force-killed (exit 137 =
SIGKILL) **after every test has already passed** — e.g. `Passed!  - Failed: 0, …`
followed by `Catastrophic failure: Test process crashed with exit code 137`. This
turns a green run into a non-zero exit, which (absent the TRX-based wrapper) marks the
task failed.

Root cause (verified live, NOT memory): the test host only peaks at ~1 GB RSS on an
8 GB host and the OS jetsam/memorystatus log shows no kills. The killer is VR's OWN
**idle-reap watchdog** in `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`
(`RunWatchedAsync`, `idleGraceMs` = `config.TestIdleGraceMilliseconds`). After the last
test prints, the host spends ~1–2 min shutting down (MSBuild/test-host teardown) — it is
output-silent and CPU-idle, so the watchdog mistakes legitimate teardown for a hang and
reaps the whole tree.

## What to fix (root cause; the TRX wrapper currently masks the symptom)

Re-grep `RunWatchedAsync` and the idle-reap predicate. The watchdog should NOT reap a
process tree whose inner command has effectively finished its work. Options to evaluate
(pick the cleanest, keep it toolchain-agnostic — no test-framework parsing):
- Distinguish "produced its terminal summary then went quiet" from "never progressing":
  only reap after the idle grace if NO test progress was seen for the whole window, not
  when the run already emitted results and is merely tearing down.
- Lengthen/separate the teardown grace from the run-idle grace, so a known-slow shutdown
  is tolerated.
- Reap only orphaned child workers (the documented intent), never the root command, once
  the root command has signalled completion.

## Done when

- A green run no longer exits 137 from the watchdog during teardown (the suite exits
  cleanly), so the TRX-counter wrapper is a belt-and-braces fallback, not load-bearing.
- A genuinely hung run (no progress, no output) is still reaped at the idle/hard cap.
- A unit/seam test covers "results emitted, then idle teardown" → not reaped vs.
  "never progressed" → reaped. `./visual-relay check` green; Conventional Commit subject.

## Coordination

Pairs with the TRX-based verify wrapper (now `tools/VisualRelay.Guards/VerifyRunner.cs`)
which currently absorbs this 137. Fixing the watchdog removes the underlying false kill.
