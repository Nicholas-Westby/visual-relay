## Task: Add a Reset button that returns a flagged task to pending without starting it

A flagged ("Needs review") task currently has two exits: Resume (continue the
flagged run where it stopped) or Mark done (archive it). There is no way to
say "discard that run; queue this task fresh, later." Add a **Reset** button:
it returns the flagged task to its initial pending state but never starts it —
the task simply becomes eligible for a future Run All. Reviewing and
queue-tending happen while long drains run, so Reset must work on a flagged
task even while another task is executing.

### Evidence (verified)

- A task is "Needs review" because `.relay/<taskId>/NEEDS-REVIEW` exists;
  `RelayTaskRepository.ListAsync` maps the marker to `NeedsReview`, drain
  collection excludes such tasks (`CollectNewTasks` in
  `RelayQueueController.PrivateHelpers.cs`:
  `!t.NeedsReview && !seenIds.Contains(t.Id)`), and `FailedRunContext`
  surfaces the flag reason in the detail panel.
- Removing the marker alone is NOT a reset: GUI runs always use
  `RelayDriverOptions(Resume: true)` (`GuiTaskRunner.cs`), so leftover
  `.relay/<taskId>/` stage state (status.json, stageN-attemptM artifacts,
  run-base.txt, …) would make the next run RESUME the flagged pipeline
  mid-flight instead of starting from stage 1.
- The flagged run's `flagged-work.bundle` can hold the ONLY copy of the run's
  authored work: when `hoist-pipeline-test-shared-setup` flagged on
  2026-07-16, the post-flag worktree reset deleted its three authored test
  files from the tree — the bundle is their sole record. A reset that deletes
  the run directory outright destroys evidence and possibly work.
- The flagged-state buttons live in
  `src/VisualRelay.App/Views/Controls/TaskActionBar.axaml` (Resume, Mark
  done). Mark done is the destructive-command template: GUI confirm modal,
  listed in `DefaultConfirmGatedCommands` (`ControlApi.cs`) so the API
  requires `{"confirm":true}` and awaits completion so `{ok:true}` means the
  effect took.
- The control API mirrors every UI command by name (`ResolveCommand` switch +
  `IcommandNames`); `/state`'s per-command enabled map and the index page
  derive from those arrays automatically.
- `DrainAsync` seeds `seenIds` from the starting queue
  (`RelayQueueController.cs`), so a task whose `NeedsReview` flips false
  mid-drain is collected at the next Sequential/RestartBetweenTasks boundary
  exactly like a newly added task. Standard mode collects nothing mid-drain.

### What to build

1. **Core reset action**: for a flagged task, archive the entire run-state
   directory in one filesystem move — e.g. rename `.relay/<taskId>/` to
   `.relay/<taskId>.reset-<utc-stamp>/` (rename-don't-delete, the
   `RestartHandoff.MarkConsumed` precedent) — so the NEEDS-REVIEW marker,
   stage state, logs, and flagged-work bundle leave the live path together
   but remain on disk for post-mortem. Afterwards the task lists as Pending
   with no run history, and its next run starts at stage 1 (no Resume
   pickup).
2. **UI**: a Reset button in `TaskActionBar.axaml` beside Resume/Mark done,
   enabled exactly when the selected task is flagged — including while
   another task is running (`IsBusy` must not gate it). Confirm modal before
   acting. It must never start a run.
3. **Control API**: a confirm-gated command (e.g. `reset-selected`) following
   the mark-done pattern end to end: `ResolveCommand`,
   `DefaultConfirmGatedCommands`, `IcommandNames`, awaited to completion;
   button and API share one CanExecute.
4. **Semantics under an active drain (deliberate, pin in tests)**: Reset only
   returns the task to Pending. Under an in-flight Sequential or
   RestartBetweenTasks drain it becomes eligible at the next boundary via the
   existing new-task collection; under Standard, nothing picks it up until
   the next Run All. State this in the button's tooltip so the behavior is
   discoverable.
5. **Hygiene**: `.relay/<id>.reset-*` archives are invisible to the task
   repository, stage-state loading (including the load-time reconciliation
   from task 06), and drain scans.

### Constraints

- This task runs after 02/06/07: build on the landed NEEDS-REVIEW ownership
  (07) and stale-state reconciliation (06) — do not add another marker
  writer or a parallel notion of "flagged".
- Never delete flagged-work bundles or killed-output autopsies — archive
  only.
- Concurrency: resetting task A while task B runs must not touch task B's
  state or the drain's control flow; a repository scan racing the rename
  must see either the pre- or post-reset view, never throw.
- Repo-agnostic; keep files under the 300-line guard; TimeProvider patterns
  for any test waits.

### Tests (red first)

- Core: flagged task with populated run state → reset → lists Pending, no
  NEEDS-REVIEW, archive dir exists with the bundle intact, and a subsequent
  run request starts at stage 1 rather than resuming.
- Gating: CanExecute true for a flagged selected task while another task is
  running; false when the selected task isn't flagged; GUI confirm and API
  `{"confirm":true}` both required (409 without, no effect).
- Drain interaction: reset during an active sequential drain leaves the
  running task undisturbed and the reset task joins at the next boundary;
  the reset itself never launches a run.
- Hygiene: repository listing ignores `.reset-*` directories.

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: flag a scratch task, reset it while another task runs, confirm it
  shows Pending with an archived run dir, then confirm a later Run All picks
  it up and starts from stage 1.
