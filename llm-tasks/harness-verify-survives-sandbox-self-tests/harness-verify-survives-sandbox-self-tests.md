# Harness: make the full-suite verify survive VR's own sandbox-spawning self-tests

## Problem

Verify (stage 9) and Fix-verify (stage 10) run the **full** test suite via `testCmd`,
under the nono sandbox, in a fresh git worktree (`SandboxedTestRunner`). Several of VR's
OWN tests spawn real child `dotnet` subprocesses (`dotnet publish` / `dotnet build`) or
otherwise need broad system access. Inside the verify's **nested** nono sandbox those
children **wedge** — they block on a resource the sandbox denies and never exit. The test
host's blame-hang collector (`--blame-hang-timeout 60s`, set in `testCmd`) then fires,
kills the host (exit **137**), and **aborts the entire verify run** — discarding the
~1800 tests that already passed and failing the gate.

This is **nondeterministic** (depends on which sandboxed child wedges and when) and
**repeatedly flagged otherwise-passing LLM tasks** — it was the single biggest reason
tasks could not complete via VR. On a normal *target* codebase the verify never hits this
(the target's tests don't spawn sandboxed `dotnet` children); it is a VR-on-itself
artifact.

### Confirmed instances (observed live)
- `CommandGuardEnsurerTests.EnsureAsync_SourceDir_StaleBinary_DetectsOutOfDate` runs a real
  `dotnet publish` (dummy project, expecting fast failure). Under the sandbox the child
  `dotnet` wedged → blame-hang → 137 → run aborted at ~test 1306. **Already mitigated** in
  commit `00ea048`: `CommandGuardEnsurer.EnsureAsync` now bounds its publish with a 45s
  linked-CTS timeout → kills the process tree → fail-open `Fallback`. That stopped THIS
  test from aborting the run, but it is a per-call band-aid, not a systemic fix.
- A **second** test (unidentified, ~test 1816 in suite order) wedged the same way → 137 →
  aborted. To identify it: read the blame-hang `Sequence_*.xml` dump the run leaves under
  the worktree's `tests/VisualRelay.Tests/TestResults/<guid>/`, and the "test running when
  the crash occurred" list printed near the `Catastrophic failure` line in the captured
  verify output (`.relay/<task>/stage9-attempt*.verify-output.txt`).
- VR ALREADY guards a family of these: `NonoRealBuildTests`, `NonoWhyOracleTests`,
  `WindowsSandboxTests` gate on the `nRUN_NONO_INTEGRATION=1` env var and **skip by
  default**. The wedging tests are the ones that lack that guard.

## Constraint (HARD — from the user)
**Do NOT weaken the sandbox.** Do not grant the keychain / `com.apple.SecurityServer`
mach-lookup, do not run the verify outside nono, do not loosen the nono profile. The
"Keychain access requires granting…" advisory nono prints on every sandboxed failure is a
**known red herring** — the real cause is child `dotnet` processes wedging, and the fix
must change **how the tests work**, not the sandbox.

## What to do
Make the full-suite verify complete deterministically inside the strict sandbox by fixing
the self-tests that spawn real subprocesses:

1. **Sweep, don't whack-a-mole.** Grep the suite for real subprocess spawns —
   `Process.Start`, `dotnet publish`, `dotnet build`, `dotnet run`, direct `nono`
   invocations, `ProcessStartInfo` — and audit each for sandbox-wedge risk.
2. **Fix each at the test layer** (pick per test):
   - Prefer **faking the process boundary**: test the surrounding logic without launching a
     real `dotnet` (inject a process runner / assert on the command that *would* run). Keeps
     coverage, sandbox-safe.
   - Or carry the **same opt-in skip guard** as `NonoRealBuildTests`
     (`nRUN_NONO_INTEGRATION=1`) so the heavy real-subprocess test skips in the default
     sandboxed run but can still be opted into locally.
   - Or, where a real subprocess is unavoidable, **bound it with a timeout** (the
     `CommandGuardEnsurer` pattern, commit `00ea048`) so a wedge degrades instead of hanging.
3. **Add a regression guard** (model it on the no-real-sleeps guard —
   `tools/VisualRelay.Guards/RealSleepGuard.cs` + its meta-test + the live-tree gate):
   fail the build if a test spawns an unbounded real `dotnet`/build/publish subprocess
   without either the skip-guard or a timeout. Stops new wedging tests from sneaking in and
   makes the property durable.

## Done when
- The full-suite verify (`testCmd` under nono in a worktree) runs to completion
  **deterministically across repeated runs** — no blame-hang / 137 aborts — with **zero**
  sandbox relaxation. Validate via exit codes + stored output, not by parsing pass/fail
  counts.
- The subprocess-spawn sweep is complete and the regression guard is in place and green.
- `./visual-relay check` green; Conventional Commit subject.

## Why this is the priority
This was the blocker that stopped every LLM task from completing via VR. The UI tasks
themselves are small and the agents author them fine; the verify gate kept aborting on
these wedges. Fix this first and the rest should flow.
