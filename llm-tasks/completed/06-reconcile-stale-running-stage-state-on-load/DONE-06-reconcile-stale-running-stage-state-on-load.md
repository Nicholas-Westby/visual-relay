## Task: Never render a stage as Running for a task with no active run

The `hoist-pipeline-test-shared-setup` task flagged at Review (stage 7) on
2026-07-15 at 20:07. This morning — 13+ hours later, with nothing running —
its Visual-review card still shows **"Running 837m 37s"** with a live green
border and a ticking timer. The stage's persisted status is the problem, not
just the timer: `.relay/hoist-pipeline-test-shared-setup/status.json` records
stage 8 as `"status": "Running"` to this day.

Commit `6a49a41` (`fix: stop sibling review-stage timer on flagged event`,
task `fix-visual-relay-timing-bug`) fixed the go-forward driver behavior
(`RelayDriver.Events.cs`: on flag, ALL Running stages are now closed, not just
stages below the flagged one) and taught the live event handler to settle the
sibling card (`MainWindowViewModel.Helpers.cs`). What it did NOT do is
reconcile state that is already wrong, or state that goes wrong through any
path other than a live flagged event. The screenshot above was taken the
morning AFTER that fix landed.

### Evidence (verified)

- `status.json` for the hoist task: stage 7 `Flagged`, stage 8 `Running`,
  stages 9-12 `Waiting` — persisted since 20:07 the previous evening.
- Run log has `s8/vision stage_start` at 19:12:37 and **no terminal s8 event
  of any kind** (no stage_done, no skip, no kill). The review-red flag path
  (`RelayDriver.ReviewPair.cs:104-116`) awaits the sibling and discards its
  result without publishing anything for stage 8, so both rehydrate-from-log
  and status.json said "Running" forever under the pre-fix driver.
- The UI derives the 837m elapsed from the stage_start event timestamp with no
  terminal event to stop it; app restarts do not help because the persisted
  artifacts are the source.

### What to build

1. **Load-time invariant**: when hydrating a task that has no active run (its
   drain is over; it is flagged/needs-review/idle), any stage in `Running`
   state must be normalized to a terminal state before display — in the stage
   board, the queue card, and `/state`. This repairs existing on-disk state
   (like hoist's) without requiring a re-run, and protects against every
   future path that fails to close a stage (crash, kill -9, power loss —
   a driver-side fix can never cover those).
2. **Terminal event for the discarded sibling**: in the review-pair flag paths,
   publish an explicit terminal event for the sibling stage whose result is
   discarded (today it gets nothing), so rehydrate-from-log agrees with
   status.json without needing the invariant to kick in.
3. **Semantics**: closing a killed/discarded sibling as `"Done"` (the 6a49a41
   choice) renders as "Completed", which misleads — the stage's result was
   thrown away. Decide a truthful terminal presentation (e.g. a distinct
   `Stopped`/`Discarded` status, or `Done` without a duration and with a note).
   Check `fix-stuck-visual-review-card-when-skipped` and
   `report-review-pair-stages-independently` (completed tasks) for the
   existing status vocabulary before inventing a new one.

### Constraints

- Do not touch how ACTIVE runs report progress; the invariant applies only
  when no run is active for the task.
- status.json remains the persisted truth: the invariant should repair it (or
  overlay it consistently everywhere), not create a UI-only divergence.
- Keep files under the 300-line guard; `ReviewPairOrphanedStageTimerTests.cs`
  is the natural home for new coverage.

### Tests (red first)

- Hydration test: a task directory whose status.json has stage 8 `Running` and
  stage 7 `Flagged`, with no active run → no stage renders as Running; no
  ticking elapsed time is exposed.
- Review-pair test: review-red flag path publishes a terminal event for the
  sibling stage (assert on the event stream, not just statusEntries).
- Presentation test: the discarded sibling does not render as an ordinary
  successful completion.

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: after the fix, the existing hoist task's board must show no Running
  stage without re-running it.
