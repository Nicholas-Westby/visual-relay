# Harness: the Verify-stage agent should not re-run the whole test suite

## Problem

At stage 9 (Verify) the driver runs the configured test command mechanically to decide
pass/fail ("The driver decides pass/fail mechanically", `RelayStages.cs`). The Verify
agent's job is only to *summarize* and propose commit subjects. But observed live, the
Verify agent **also improvises its own full `dotnet test` run** (e.g. `dotnet test … --no-restore`)
before summarizing. That re-runs the entire suite a second time, roughly doubling the
already-slow verify stage (~10–12 min each on this host) for zero added signal.

## What to fix (re-grep the Verify/Fix-verify stage instructions in `RelayStages.cs`)

Keep it general (no test-framework specifics). The Verify-stage agent prompt should make
clear it does NOT need to execute the test suite itself — the harness has already run (or
is running) it mechanically and decides pass/fail. Give the agent the driver's captured
test output as context so it can summarize without spawning its own run. If the agent
must be allowed any command, steer it to the targeted command, never the full suite.

## Done when

- The Verify stage runs the suite once (the driver's mechanical run), not twice.
- The agent still produces an accurate summary + commit-subject candidates from the
  driver-provided results.
- `./visual-relay check` green; Conventional Commit subject.

## Coordination

Benefits every task's verify. Pairs with `harness-verify-runner-prints-test-output`
(give the agent the real test output so it has no reason to re-run).
