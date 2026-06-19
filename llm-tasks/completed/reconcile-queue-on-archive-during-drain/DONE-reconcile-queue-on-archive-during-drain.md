# Reconcile the in-memory queue when tasks archive mid-drain (stale "Pending" rows + stale spec-path error)

During a Run All drain, when a task COMPLETES and is archived — its spec moves from
`llm-tasks/<task>/<task>.md` to `llm-tasks/completed/.../DONE-<task>.md` — the app's
IN-MEMORY task state is not reconciled, producing two visible defects (both observed on
a real 4-task drain of an external repo):

1. **Stale roster**: the completed task keeps showing as **"Pending"** in the queue
   sidebar for the rest of the drain (after 3 of 4 tasks had committed+archived, all 3
   still read "Pending"). It should show Done/archived, or drop out of the active queue.
2. **Stale spec-path error**: the bottom-left status text showed
   `Could not find a part of the path '/…/llm-tasks/add-hover-tooltips/add-hover-tooltips.md'`
   — an archived task's in-memory `MarkdownPath` still points to the OLD location, and an
   operation that re-reads it (a refresh, or the selected task's detail re-read) throws
   `FileNotFoundException` because the file was moved to `completed/`.

Both stem from one gap: archiving during an active drain updates the on-disk layout but
not the live `Tasks` / `SelectedTask` view-model state.

## Current state (researched)

> Freshness contract: locate anchors by quoted snippet/symbol, never by line number.

- Archive-on-completion: `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs` moves the
  spec into `completed/` and renames it `DONE-…`.
- A task's spec path is `RelayTask.MarkdownPath`, read in
  `RelayTaskRepository.ReadTaskInputAsync` (`File.ReadAllTextAsync(task.MarkdownPath, …)`)
  and surfaced via `TaskRowViewModel.MarkdownPath`. After archive, the in-memory
  `MarkdownPath` is stale.
- The drain's per-task completion hook is `OnExecuteCompleted` in
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs` — it calls
  `ClearRunningTask` + `RefreshSelectedTaskErrorAfterRun` but does NOT reconcile the
  archived task's row state or its stale path.
- `RefreshAsync` (`MainWindowViewModel.Commands.cs`) reloads from `RelayTaskRepository`,
  which already classifies `completed/` and DONE-residue correctly (commit `5c82e51`,
  `folder-task-done-residue-resurrects-as-pending`) — but a full RefreshAsync is NOT run
  per task during the drain, so the live roster drifts from disk.
- The SelectedTask (set at app start, e.g. the first task) is never re-pointed when it
  archives, so a detail re-read hits the moved path → the status-text error above.

## What to do

When a task archives during a drain (in/around `OnExecuteCompleted`):
- Reconcile the completed task's row so it no longer shows "Pending" — mark it
  Done / move it to the archived view / drop it from the active pending set. REUSE the
  same classification `RelayTaskRepository` applies on a full refresh (don't hand-roll a
  divergent rule that could diverge from disk).
- Update or clear the archived task's `MarkdownPath` / detail state so nothing re-reads the
  old `llm-tasks/<task>/<task>.md`. If `SelectedTask` was the archived task, re-point it to
  the `completed/` location or clear the detail read so no `FileNotFoundException` reaches
  the status text.
- Prefer a real reconcile (an incremental/whole refresh the drain can call safely WITHOUT
  disrupting the running task or losing manual queue order) over a cosmetic patch.

## Acceptance criteria
- During Run All, a task that commits+archives stops showing "Pending" in the roster before
  the drain ends (shows Done/archived or leaves the active list).
- No `Could not find a part of the path '…/<task>.md'` error is surfaced when a task archives
  mid-drain.
- The single-run path and a manual Refresh are unaffected; the terminal
  `Queue drained · N committed` status stays correct.
- A headless `[AvaloniaFact]` test drives the drain lifecycle with a task archiving mid-drain
  and asserts (a) the row is no longer Pending and (b) reading/refreshing does not throw on the
  stale path. (`CreateDrainLifecycleCallbacks()` is `internal` so a VM test can drive the hooks.)
