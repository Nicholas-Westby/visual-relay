# Subtract the base's failures in Fix-verify, and tell its agent they are not its to fix

Stage 10 subtracts the failures the run's base commit already has, so a task
that only leaves old failures red still commits. Stage 11 (Fix-verify) does
not: its check is the exit code alone, and its prompt orders the agent to make
the full suite exit 0. So once a task enters Fix-verify for any reason, old
failures can never be subtracted, and the agent "fixes" them out of scope or
burns its runs. On i18next both tasks did exactly that: each Fix-verify agent
pinned the suite's time zone in `vitest.config.mts`, verify went green, and
the pin was committed with the task.

## Evidence

Windows arm, i18next (vitest 4.1.11, two vitest projects), 2026-09-14, on
0.367 to 0.372. Evidence archived on the Windows PC at
`~/vr-eval/i18next-run1-evidence` (`.relay` and `llm-tasks` of both tasks).

- Task 1 (skipOnVariables): stage 10 red at 06:59 on a vite startup error
  (see `give-the-verify-snapshot-its-own-node-modules-caches`). Stage 11
  attempt 1 verify red with exactly the three failures the base has
  (`intlDateTime` x3; "Tests 3 failed | 990 passed | 9 skipped"). Attempt 2
  added `process.env.TZ = 'UTC'` to `vitest.config.mts` with the answer "They
  are real test failures, so per the gate rules they had to be resolved
  legitimately rather than reported", and verify went green.
- Task 2 (returnDetails): stage 10 red with only the three base failures; no
  baseline ran (fixed since, see below); Fix-verify pinned TZ in one attempt;
  resumed at Commit it committed 8157f31 including `vitest.config.mts`.
- A later Fix-verify attempt on task 1 grepped `.relay/<task>/run.log` for
  `chmod` and `vite-temp`, found an earlier agent's environment workaround
  there, and repeated it (`rm -rf node_modules/.vite-temp && chmod 555
  node_modules` on the real checkout). Its own words: "my own reasoning is
  being logged into run.log in real time".
- Why stage 10 never subtracted on task 2: vitest's project badge hid every
  failure name. Fixed in 101e1a4d (0.378), so from 0.378 stage 10 does
  subtract on i18next; stage 11 still does not.

## Current state (researched at 37403a8a)

- `Execution/RelayDriver.cs:238-241`: stage 10 calls `GetNewFailuresAsync`
  when `config.BaselineVerify` and the red is not a bootstrap, guard or
  new-guard failure; a null result (all failures pre-existing) turns the stage
  green and skips Fix-verify.
- `Execution/RelayDriver.BaselineVerify.cs:15-30` `GetNewFailuresAsync`:
  extracts ids with `TestFailureIds.Extract`, returns "verify failed" when a red
  run names none, runs the base in a snapshot (`RunOnTheBaseAsync`, `:67-110`,
  a full suite run every call), subtracts, and publishes `verify_baseline`
  with the stage number hard-coded to 10 (`:56`, and `:85` for the snapshot
  event).
- `Execution/RelayDriver.VerifyFix.cs:179`: `check ??= testResult.ExitCode == 0
  ? "green" : "red";` after `RunIsolatedVerifyAsync`. The guard re-check in the
  same loop (`:146-152`) already uses a baseline diff.
- `Execution/RelayStages.cs:144-157`, the Fix-verify system prompt: "run
  exactly that command and confirm it exits 0 before returning success" and
  "Fix all failures from the full test suite gate".
- Precedent for caching a base-commit answer across a drain:
  `Execution/RelayDriver.GuardAttribution.cs:19` `BaseGuardVerdicts`, keyed by
  repo root, base sha and command, `Lazy<Task<>>`, entry dropped on fault.

## Prescribed approach

1. Cache the base run like `BaseGuardVerdicts`: a static
   `ConcurrentDictionary<string, Lazy<Task<TestRunResult?>>>` keyed by full
   root path, base sha and test command, used by `GetNewFailuresAsync`. A base
   commit's result for one command does not change within a drain. Drop the
   entry when the run faults or times out, so the next caller retries.
2. Give `GetNewFailuresAsync` and `PublishBaselineAsync` a `stageNumber`
   parameter instead of the hard-coded 10.
3. In the stage 11 loop, when the test run is red, `config.BaselineVerify` is
   on, and neither bootstrap nor guard made the attempt red, call
   `GetNewFailuresAsync` with this attempt's verify output path. Null means
   green (every failure was on the base). "verify failed" or a list keeps it
   red; the list goes into the reason, as stage 10's flag does ("new test
   failures: ...").
4. Pass the base's failing ids into the Fix-verify invocation (a new optional
   field beside `LastTestOutput`) and render them in the prompt as a section
   "## Failures already on the base commit": the ids, one per line, with "The
   harness subtracts these. Do not change code, tests or configuration to make
   them pass." Rewrite the Fix-verify prompt's "confirm it exits 0" sentence
   to "confirm no failure outside that list remains".
5. Add to the Fix-verify prompt: an error that comes from the environment (a
   permission denied on a dependency folder, a missing tool, a network fetch)
   is not the task's to fix: report it; never change file permissions,
   dependency folders or anything outside the task's files; never repeat a
   workaround found in `.relay` logs.

## Tests

- `RelayDriverFixVerifyBaselineTests` (new, driver fakes as in
  `RelayDriverBaselineFailureIdTests`): stage 11 red whose failures are all on
  the base records green and commits; red with one extra failure stays red
  and the flag reason names only that failure; the base suite runs once for
  stage 10 plus two stage 11 attempts (count runner calls on the base
  snapshot); `verify_baseline` for stage 11 carries stage 11.
- Prompt test beside the existing `SandboxedStage.Prompt` tests: a Fix-verify
  invocation with base ids renders the section and the ids; without ids the
  section is absent.
- Watch each test fail first; mutate the stage 11 call away and the cache key
  (drop the command) and confirm a test catches each.

## Verification (through the control API)

On the Windows arm (the i18next clone is reset to 4ebd19d with the three TZ
failures on its base): re-bootstrap, recreate the two tasks from the archived
task files, `run-all`. Expected: stage 10 `verify_baseline preExisting=3`,
Fix-verify skipped or, if entered, green without touching
`vitest.config.mts`; neither commit contains `vitest.config.mts`. On the Mac,
luxon (nine ICU failures on its base) gives the same shape with jest.

## Out of scope

Reviewing what Fix-verify changed (`review-what-fix-verify-changed`); the vite
cache folder itself (`give-the-verify-snapshot-its-own-node-modules-caches`).
