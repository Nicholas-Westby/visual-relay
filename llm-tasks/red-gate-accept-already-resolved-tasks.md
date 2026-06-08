# Accept already-resolved tasks at the Author-tests gate instead of flagging "did not go red"

Stage 5 ("Author-tests") enforces a TDD red gate in `RelayDriver.cs` (the
`stage.Number == 5` block, ~lines 113-156). When the manifest contains an
implementation (non-test) file (`hasImpl`), `AuthorTestGate.RunAsync` strips the
impl files to HEAD and runs the authored test, requiring it to go **red**. If the
test comes back green it flags the task.

The strip step (`RedGate.StripToRedAsync`, `RedGate.cs:42-46`) returns
`false` — i.e. `gateResult.StashedImplementation == false` — when the manifest's
impl files have **no working-tree change** (`git status --porcelain` over those
paths is empty). That happens when the root-cause fix is **already present**
(committed by an earlier task/batch), so the agent makes no net source edit and
the only remaining deliverable is green regression coverage. In that situation
the test runs against the real (already-correct) code, passes green, and the gate
flags `"author-tests did not go red"`.

The result: a task whose underlying issue was already fixed can **never**
complete. Diagnose correctly concludes "the fix is already in place, only test
coverage is missing", writes a passing regression test, and stage 5 flags it on
every drive.

Concrete, reproducible case: JobFinder `fix-job-zero-careers-page-software-engineer-net`.
Its Colombo geo-restriction fix is already committed (`colombo` in
`src/scoring/geo-restriction-data.ts`); each relay drive adds a green
`Colombo, WP, Sri Lanka` regression test and then flags at stage 5 with
"author-tests did not go red" (observed on two separate drives).

The gate conflates two cases that `StashedImplementation` already distinguishes:

- `StashedImplementation == true` and green — impl files **were** reverted yet the
  test still passes → the test does not exercise the implementation → genuinely
  vacuous → flag correctly (this is the existing
  `"author-tests passed after implementation files were stripped"` reason).
- `StashedImplementation == false` and green — there was **nothing to revert** (no
  impl delta; the fix is already present) → the task is already-resolved and the
  green test is valid coverage → must **not** flag.

## Goal

On a green stage-5 gate result, only flag when `gateResult.StashedImplementation`
is true (the vacuous-test case). When it is false (no implementation delta existed
to strip), treat the task as already-resolved: accept the green regression test
and let the pipeline proceed to Implement/Review/Verify/Commit, which will commit
the new test as coverage. All behavior for tasks that do have an impl delta
(red-when-stripped, or vacuous-when-stripped) is unchanged.

## Approach (suggested)

- In the `stage.Number == 5` branch of `RelayDriver.cs`, change the `check != "red"`
  handling: keep `FlagAsync(... 5, reason ...)` only when
  `gateResult.StashedImplementation` is true. When it is false, do not flag — set
  `check = "green"` and fall through so the run continues normally.
- Leave the timeout and `RedGateRestoreResult.Conflict` guards exactly as they are.
- Record an honest status note for the accepted case (e.g. "already-resolved: no
  implementation delta to strip; accepted green regression coverage") so the run
  log explains why the red requirement was waived.

## Files

- `src/VisualRelay.Core/Execution/RelayDriver.cs` (stage 5 gate branch, ~148-154)
- `src/VisualRelay.Core/Execution/AuthorTestGate.cs` only if the distinction is
  cleaner surfaced from the gate result

## Tests

Use the mocked test runner + subagent doubles already in the test project. Drive
the gate via the existing red-gate test fixtures.

- **already-resolved**: manifest lists an impl file that has *no* working-tree
  change, authored test passes green → `StashedImplementation == false` → task
  proceeds to the next stage (NOT flagged).
- **vacuous test (regression guard)**: manifest lists an impl file *with* a change,
  the gate strips it, the test still passes green → `StashedImplementation == true`
  → still flags `"author-tests passed after implementation files were stripped"`.
- **proper TDD (unchanged)**: impl delta present, test goes red when stripped →
  proceeds as today.

## Notes

Keep `RelayDriver.cs` under the 300-line guard — extract a small helper into
`RelayDriver.Artifacts.cs` if the branch grows. This is the exact failure mode
behind JobFinder `careers-page-net` flagging on every drive despite its fix being
present; with this change a re-drive of that task will commit the regression test
and complete.
