# Scope Task Errors And Needs-Review State To The Selected Task

## Problem

While Run All was processing multiple tasks, the GUI/control API repeatedly showed stale failure
information on unrelated tasks:

- After `08-no-silent-real-env-fallback` flagged in Verify, selecting or following later running tasks
  still returned `selectedTask.error` with the old timeout text.
- During the run, flagged tasks appeared in the task list as `Pending` with `needsReview: false` even
  though `.relay/<task>/NEEDS-REVIEW` existed and the stage status was `Flagged`.
- The failure banner could therefore be shown for the wrong selected task. This also makes the new
  "Create task to fix" button dangerous when the app is idle, because a stale error can make a clean
  task look failed.

After the queue drained, the state corrected itself to `Needs review`, which suggests the underlying
flag files are present but live queue state projection is stale or scoped incorrectly.

## Goal

Make task error state and needs-review state derive from the selected task, not from the last flagged
task or global queue state. A task without its own current failure must not inherit another task's
error banner.

## What to build

1. Find the source of `selectedTask.error`, `needsReview`, and `stateLabel` in the app view model and
   control API state projection.
2. Ensure each task row and selected-task detail reads its own `.relay/<taskId>/status.json` and
   `.relay/<taskId>/NEEDS-REVIEW` state, or the equivalent in-memory state for that same task.
3. Clear `SelectedTaskError` when selecting a task that has no failed/latest error.
4. While Run All is still busy, tasks that have already flagged must show `Needs review` /
   `needsReview: true` instead of `Pending`.
5. Add regression tests for:
   - selecting a clean/running task after another task flags does not show the flagged task's error;
   - the control API reports `needsReview: true` for a flagged task while another task is running;
   - the failure banner and `CreateFixTaskCommand` are disabled for a task with no own error.

## Done criteria

- No stale timeout/error text appears on unrelated selected tasks.
- Flagged tasks are accurately marked as needs-review during and after Run All.
- `Create task to fix` is enabled only for the selected task's own failed run.
- The full `./visual-relay check` gate passes.
