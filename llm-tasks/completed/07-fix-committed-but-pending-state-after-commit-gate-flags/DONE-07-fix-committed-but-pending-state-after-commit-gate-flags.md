## Task: Make a commit-stage flag leave coherent state instead of "committed but pending"

When the Commit stage (12) seals its commit and a POST-commit check then flags
the task, the system ends in a self-contradictory state. Observed on
2026-07-15 for two tasks flagged by the commit gate
(`merge-nocommit-contamination-tests-data-driven`,
`split-key-setup-panel-ui-tests`):

- The sealed commit is in history on main — the work "landed".
- The task card says **Needs review** (NEEDS-REVIEW marker exists).
- The stage board shows Commit **Complete** — persisted `status.json` records
  stage 12 as `"Done"` with `error: null`, so after an app restart nothing
  shows the flag at all.
- The task definition exists in git **twice**: the sealed commit moved it to
  `llm-tasks/completed/<id>/DONE-<id>.md`, then the flag path's retirement
  rollback re-created `llm-tasks/<id>/<id>.md` on disk, and the next
  "chore: add tasks" auto-commit committed that duplicate (byte-identical to
  the completed copy).

An operator cannot tell what actually happened: the queue says review the
task, the stage board says everything succeeded, git says it shipped, and the
task list shows it as still to-do.

### Evidence (verified)

- Run-log ordering for the split task
  (`.relay/split-key-setup-panel-ui-tests/run.log` lines 382-384):
  `s12/cheap stage_done … status=Done` at 05:29:14.93 — **before** the sealed
  commit (05:29:16) and the `s12/? flagged` event (05:29:18.71). Stage 12 is
  reported Done before the gate has done any of its work; the "Done" is a
  premature bookkeeping artifact of the generic stage loop, not the gate's
  outcome.
- `FlagAsync` (`src/VisualRelay.Core/Execution/RelayDriver.Events.cs:~95-121`)
  unconditionally calls `MarkStatusFlagged(statusEntries, 12, reason)` and
  `WriteStatusAsync` — yet the final on-disk `status.json` (mtime = flag
  minute, untouched since) says stage 12 `Done`/no-error for BOTH tasks. So
  some later writer persists the stale pre-flag entries after `FlagAsync`
  returns. Contrast: `hoist-pipeline-test-shared-setup` flagged at stage 7
  (which was `Running`, not pre-marked `Done`) and its `Flagged` status DID
  survive on disk — the clobber is specific to the commit-stage path.
  Find the last writer (driver epilogue after `ExecuteCommitStageAsync`
  returns, or the queue controller's post-flag handling) and fix the ordering
  or make flag state win.
- The queue controller separately overwrites the NEEDS-REVIEW marker with a
  reason-only body (`RelayQueueController.PrivateHelpers.cs:16-21` —
  hoist's marker is exactly the 34-byte reason line, without the `stage N`
  line `FlagAsync` writes) — two writers with different formats for the same
  marker file.
- Retirement rollback (`retirement?.Rollback?.Invoke()` in
  `RelayDriver.CommitGate.cs:222`, invoked AFTER the sealed commit already
  recorded the folder move) restores the active task folder on disk but the
  completed copy from the sealed commit remains tracked — the duplication
  described above. `TaskCompletionArchive.RetireAsync` owns the move.

### What to build

1. **Truthful stage state**: after a stage-12 flag, persisted `status.json`
   must say stage 12 `Flagged` with the reason — surviving app restarts and
   any post-flag writes. Do not publish/persist `Done` for the commit stage
   before the gate (commit + post-commit checks) has actually finished; the
   stage_done event should carry the gate's real outcome.
2. **Coherent lifecycle on flag-after-seal**: decide and implement ONE
   documented outcome when the gate flags after the commit exists. The two
   defensible designs — pick one and encode it in tests:
   - roll back the seal too (reset the branch to run-base, preserving the
     work in the flagged-work bundle, so "flagged" always means "nothing
     landed"), or
   - keep the seal and do NOT roll back retirement (the task stays completed;
     the flag becomes a review advisory pointing at the sealed commit).
   Either way the current halfway state (commit kept + retirement rolled
   back) must be impossible.
3. **No duplicate task definitions**: whatever the choice in (2), the task
   must never end up tracked in both the active and completed locations.
   Guard the "chore: add tasks" auto-commit against re-adding a task whose
   identical completed copy is already tracked.
4. **One writer for NEEDS-REVIEW**: unify the driver and queue-controller
   marker writes so the marker format (and content) is owned in one place.

### Constraints

- The post-commit invariant checks themselves stay — this task fixes the
  state they leave behind, not whether they run (their false-positive causes
  are `01-unquote-git-paths-for-non-ascii-filenames` and
  `02-capture-untracked-baseline-on-resume-runs`).
- Repo-agnostic; sequencing/observability changes only, no new stage.
- Keep files under the 300-line guard.

### Tests (red first)

- Driver test: commit succeeds, post-commit check flags → persisted status
  records stage 12 `Flagged` + reason; re-reading the task directory (fresh
  hydrate) shows the flag; and the world matches the chosen design in (2)
  (assert branch state and folder locations explicitly).
- Regression: a flag at a non-commit stage (e.g. 7) still persists correctly
  (hoist's case).
- Duplication guard: after a flag-after-seal cycle, exactly one tracked copy
  of the task definition exists.

### Verification

- `./visual-relay check` fully green including the new tests.
