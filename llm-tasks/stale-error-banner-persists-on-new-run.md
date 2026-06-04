# "LATEST RUN FAILED" banner lingers after a new run on the same task starts

When a task's previous run errored, the red "LATEST RUN FAILED" banner in the
center TASK pane keeps showing the *old* failure even after the user starts a
fresh run on that same task. In the field this reads as a contradiction: the
queue card and stage board show the task **Running** (e.g. "Stage 02 · Research,
Running"), yet the TASK pane still says the run failed with a stale
`cheap-kimi … Connection error.` from the prior attempt. The banner only
corrects itself once the new run finishes and run history is reloaded.

## Cause

The banner is driven by `SelectedTaskError` / `HasSelectedTaskError`
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs:113-116`), rendered by
the red surface in `TaskDetailPanel.axaml`. It is **populated** from the latest
errored stage's `result.error_message` in `LoadRunHistoryAsync`
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs:12`), and the
only place it is **cleared** is the no-task branch of `LoadSelectedTaskAsync`
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs:154`).

Starting a run does not clear it. `RunOneAsync`
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs:67-72`) begins
a run with `ResetStages()` + `ClearLogState()` + `BeginRunningTask(task)` but
leaves `SelectedTaskError` untouched, so the prior failure's text stays on screen
for the whole new run. `LoadRunHistoryAsync` (which would refresh it) is only
called *after* the run completes (`Execution.cs:84`), not at the start.

## Recommended fix

Treat "a run started" as "there is no latest failure yet" and stop showing a
stale error while a run is in progress:

1. **Clear the error when a run starts.** Set `SelectedTaskError = null` at the
   top of `RunOneAsync` alongside `ResetStages()` / `ClearLogState()` (or inside
   `ResetStages()` if that reads cleaner and doesn't regress the task-switch
   path, which re-derives the error via `LoadRunHistoryAsync`). The banner then
   reappears only if the *new* run's post-run `LoadRunHistoryAsync` finds a
   failed latest stage.
2. **Don't let it creep back mid-run.** If the user clicks away and back to the
   running task, `LoadSelectedTaskAsync` → `LoadRunHistoryAsync` re-reads the
   latest report on disk, which (until the in-progress stage writes its own
   attempt report) is still the *previous* attempt's errored report — so the
   stale banner can return mid-run. Suppress the error surface while the selected
   task is the actively running one: e.g. have `LoadRunHistoryAsync` leave
   `SelectedTaskError` null when the task is currently running
   (`_runningTaskId == taskId`), or gate `HasSelectedTaskError` on
   "not currently running". Pick whichever keeps the rule in one place.

Keep the existing behavior for a *settled* task: a task whose latest run errored
and is **not** running still shows the banner; a clean/never-run task shows
nothing.

## Done when

- Starting a run on a task that previously failed immediately hides the
  "LATEST RUN FAILED" banner; it does not reappear while that task is running,
  even if the user navigates away and back.
- If the new run also fails, the banner returns with the *new* run's error after
  the run settles; a successful run leaves no banner.
- A failed-and-not-running task still shows its error (no regression to the
  surface added by `DONE-surface-stage-error-in-detail-pane.md`).
- Unit coverage on the view model: a selected task with a failed latest run that
  transitions to "running" exposes no `SelectedTaskError` / `HasSelectedTaskError`
  is false; after a failing run settles it is set again. Write the failing test
  first.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
