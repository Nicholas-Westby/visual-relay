# Verify stage must honor baselineVerify (ignore pre-existing failures)

The `baselineVerify` config flag (default `true` in `RelayConfigLoader`, field on
`RelayConfig`) is accepted but **never consulted**. In `RelayDriver` stage 9
(`RelayDriver.cs`, the `stage.Number == 9` block) the driver runs
`config.TestCommand` and, on any non-zero exit, immediately
`FlagAsync(... 9, "verify failed" ...)`.

That means **any** failing test in the target repo's suite — including ones that
already fail on the pre-run baseline and have nothing to do with the task —
flags the task and halts the drain. This bit the self-hosting pipeline twice
when draining JobFinder (a brittle pre-existing test, then a stale biome schema):
both were pre-existing, yet every task flagged.

## Goal

When `config.BaselineVerify` is true and the stage-9 test command fails, only
flag the task for failures that are **new relative to the pre-run baseline**.
Pre-existing failures (present with the task's changes reverted) must not flag
the task.

## Approach (suggested)

- On a red stage-9 verify, capture the set of failing tests/identifiers from the
  test output.
- Establish the baseline: stash/revert the run's working-tree changes (the task
  touched the tree across stages), re-run the test command, capture its failing
  set, then restore the changes. (Reuse the red-gate's existing stash/restore
  plumbing if practical.)
- Compute `new = current_failures - baseline_failures`. If `new` is empty, treat
  verify as green (proceed); otherwise flag with the new failures only.
- Only do the (expensive) baseline run when verify is red and `BaselineVerify` is
  true — never on green.

## Files

- `src/VisualRelay.Core/Execution/RelayDriver.cs` (stage 9 logic)
- `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs` if helpers are needed
- `RelayConfig.BaselineVerify` already exists — wire it in.

## Tests

- A pre-existing failing test (fails before and after the change) must NOT flag
  the task (verify treated as green).
- A failure introduced by the change (passes on baseline, fails after) MUST flag.
- With `BaselineVerify=false`, behavior is unchanged (any failure flags).

## Notes

Keep the file under the 300-line guard. Add a focused test double for the test
runner so the baseline/working failure sets can be controlled deterministically.
