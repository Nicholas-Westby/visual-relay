# Coding Stages Run the Full Test Suite Before Declaring Done

## Problem

VR intentionally splits testing to keep agent iteration fast:

- The **coding stages** — Implement (6) and Fix (8) — receive a **targeted** test command built from the
  task manifest (`BuildTargetedTestCommand`, `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs`,
  which falls back to `config.TestCommand` when it can't narrow scope). See
  `src/VisualRelay.Core/Execution/RelayDriver.cs`:
  `var testCommandForCodingStage = stage.Number is 6 or 8 ? targetedTestCommand : null;`.
- The agent prompt then tells the agent to run **exactly that** command — see the `## Verify command`
  block in `src/VisualRelay.Core/Execution/ProcessRunners.Prompt.cs`
  (`"Run this exact command to reproduce and confirm the fix:"` + `invocation.TestCommand`).
- The **harness** later runs the **full** suite (`config.TestCommand`) as the authoritative `Verify` gate
  (stage 9), and the Fix-verify stage (10) already receives the full command
  (`RelayDriver.VerifyFix.cs` builds its invocation with `testCommand: config.TestCommand`).

**Consequence.** Cross-cutting failures that a *targeted* run cannot surface — project-wide guards,
lint/style gates, tests that enforce special standards, and test-ordering hazards — are invisible to the
Implement/Fix agent. They first appear at the harness's `Verify` gate, which then churns the fix-verify
loop. Combined with any genuinely flaky tests, this produces the "different failure each round"
whack-a-mole that can exhaust a task's attempt budget even when its own change is correct.

This is **project-agnostic**: any codebase whose full suite includes checks a narrowed subset misses
hits it.

## Goal

Have the coding-stage agent **iterate quickly with the targeted command**, then **run the project's full
test suite once before declaring the change done**, so cross-cutting failures are caught and fixed inside
the stage instead of being deferred to the harness gate — where they cost a full (and possibly flaky)
verify round.

Keep it **fully general**: use the project's own `config.TestCommand`. Do **not** bake in any specific
check, rule, or tool — VR runs on arbitrary codebases and must not assume a project's conventions.

## Design

1. **Convey both commands to the coding stages.** Extend the invocation
   (`src/VisualRelay.Core/Execution/RelayDriver.Invocation.cs` and the `StageInvocation` record) so
   stages 6 and 8 receive **both** the targeted command (for fast iteration) **and** the full command
   (`config.TestCommand`, for the final run). Today only a single `TestCommand` is passed and the coding
   stages get the targeted one; add the full command alongside it (e.g. a `FullTestCommand`), populated at
   the coding-stage wiring in `RelayDriver.cs` where `testCommandForCodingStage` is set.

2. **Amend the `## Verify command` prompt block** (`ProcessRunners.Prompt.cs`) for the coding stages so it:
   - gives the **targeted** command for fast iteration while working, and
   - instructs the agent that **before declaring the change complete, it must run the full test suite
     once** (the full command) to catch cross-cutting checks a targeted run misses — name the categories
     explicitly (project-wide guards, lint/style gates, tests enforcing special standards, test-ordering
     issues) — and if the full suite fails, fix and re-run it.
   - Only include the full-suite instruction when the **full command differs from the targeted command**
     (when `BuildTargetedTestCommand` fell back to `config.TestCommand` they are identical, so the extra
     advice would be redundant).

3. **Preserve the performance win.** Frame the full run as a **final, once-before-done** step, not a
   per-iteration one, so the agent still mostly runs the fast targeted command and only "churns" on the
   full suite near the end.

4. **Scope.** Apply to the stages that currently receive the targeted command — Implement (6) and Fix (8).
   Leave Fix-verify (10) as-is (it already runs the full command). Do not change the harness's own
   authoritative `Verify`/Fix-verify gate behavior; this task only changes what the *coding agent* is
   asked to run.

## What this does and does not fix

- **Does:** removes the class of harness-gate surprises caused by **deterministic** cross-cutting
  failures (guards, lint/style, file-size limits, and often order-dependent crashes) — the agent now sees
  and fixes them in-stage.
- **Does not:** fully eliminate flaky-gate churn. A genuinely non-deterministic test can still pass in the
  agent's full run and fail in the harness's authoritative re-run; making a specific flaky test
  deterministic is a separate concern.

## Constraints & done criteria

- **General, not VR-specific.** No hard-coded checks/rules; the only command used is the project's
  configured `config.TestCommand`.
- Do not emit the full-suite recommendation when the full and targeted commands are identical.
- The agent's turn budget/timeout must accommodate one full-suite run near the end. This is the same suite
  the harness already runs, so it is within existing tolerances; note it but no new budget knob should be
  required.
- Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines; no weakening/skipping/deleting tests.
- Tests (use the existing prompt-building test seams):
  - the Implement/Fix prompt includes the full-suite instruction when the full command **differs** from
    the targeted command, and **omits** it when they are identical;
  - stage 6/8 invocations carry both the targeted and full commands;
  - Fix-verify (10) is unchanged.

## Files likely in scope (the plan stage will finalize the manifest)

- `src/VisualRelay.Core/Execution/RelayDriver.Invocation.cs` (+ the `StageInvocation` record) — carry both commands
- `src/VisualRelay.Core/Execution/RelayDriver.cs` — populate the full command for the coding stages
- `src/VisualRelay.Core/Execution/ProcessRunners.Prompt.cs` — the `## Verify command` prompt block
- tests under `tests/VisualRelay.Tests/`
