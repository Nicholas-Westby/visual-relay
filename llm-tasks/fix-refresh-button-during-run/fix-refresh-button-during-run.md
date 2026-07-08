# Make the Refresh Button Work While a Run Is Active

User-reported and reproduced (2026-07-07): clicking **Refresh** in the top bar does nothing —
the task list doesn't update. Clicking **Archive** and then **Queue** DOES update the list.
The reports coincide with an active queue drain, and the code confirms that's the trigger.

## Root cause (verified in source — all in `src/VisualRelay.App/ViewModels/`)

Refresh is double-gated on the busy flag that a queue drain holds for its entire duration:

- `MainWindowViewModel.Helpers.cs`:
  `private bool CanRefresh() => !IsBusy && Directory.Exists(RootPath);`
  While a drain runs, `IsBusy` is true, so `RefreshCommand` is disabled — a click is ignored
  (the button's disabled look is subtle enough that users keep clicking it).
- Even when invoked directly (the control API maps `"refresh"` to the same command —
  `ControlApi.cs`: `"refresh" => viewModel.RefreshCommand`), the body immediately no-ops:
  `RefreshAsync` wraps its work in `RunBusyAsync`, whose first lines are
  `if (IsBusy) { return; }` (`MainWindowViewModel.Helpers.cs`). So a mid-drain refresh
  silently does nothing, headless included.
- The workaround works because the Archive toggle has **no** busy gate:
  `CanToggleArchive() => Directory.Exists(RootPath)`, and `ToggleArchiveAsync`
  (`MainWindowViewModel.Commands.cs`) calls `ReloadTaskListAsync()` **directly**, not through
  `RunBusyAsync`. Two toggles = a full `Tasks.Clear()` + reload while the drain continues —
  proving a mid-drain list reload is already exercised and survivable today.

## What to build (TDD-first)

1. **Busy-tolerant Refresh.** Drop `!IsBusy` from `CanRefresh`. Rework `RefreshAsync` so the
   list reload does not go through `RunBusyAsync`'s `IsBusy` flip when a drain is active —
   reload directly, the way `ToggleArchiveAsync` already does. (When idle, current behavior —
   including the `IsBusy` flip that serializes against starting a drain mid-reload — may be
   kept; the simplest correct shape is: always reload directly, and only take the busy flip
   when not already busy. Implementer's choice, but both paths must be tested.)
2. **Don't lie in the status line.** Mid-drain, `StatusText` shows the running state; a
   refresh must not leave it saying an idle `"N pending"` while a task is still running.
   After a mid-drain reload, restore/recompute the running status text (note:
   `ToggleArchiveAsync` currently ends with `StatusText = PauseRequested ? … :
   FormatQueueStatus();` — it has this exact bug; align both call sites while here, since the
   status-restore logic is shared).
3. **Preserve the running row.** `ReloadTaskListAsync` rebuilds `Tasks`; the drain's live
   stage updates must keep landing on the running task's row after a mid-drain reload. The
   Archive→Queue toggle already exercises this path today (verify how the running row's state
   is rehydrated on reload — e.g. the `preferredTaskId` selection logic and whatever restores
   running-stage display — and reuse it; if the toggle path has a gap here, fix it once in
   `ReloadTaskListAsync`, not per-caller). Selection should follow the same rule as the
   toggle: keep the running/selected task selected when it still exists.
4. **Keep mutating commands gated.** Run All / Run Selected / Resume / Mark Done etc. remain
   busy-gated exactly as today; this task changes only Refresh (and the shared status-restore
   in the toggle, per item 2).
5. **Tests** (existing MainWindowViewModel headless test patterns):
   - with `IsBusy` true (simulated drain), `RefreshCommand.CanExecute` is true; executing it
     reloads `Tasks` (a task folder added on disk after the initial load appears);
   - the running task row survives a mid-drain refresh with selection preserved;
   - `StatusText` still reflects the run after a mid-drain refresh (both via Refresh and via
     the Archive→Queue toggle);
   - idle refresh behaves exactly as before (regression pin);
   - the control-API `"refresh"` mapping reaches the new path (reuse existing ControlApi test
     patterns if present).

## Done when

- During an active run, clicking Refresh (or POSTing control-API `refresh`) updates the task
  list immediately — new task folders appear without touching Archive/Queue.
- Status text never shows idle queue status while a task is running, from either reload path.
- All tests above pass; `./visual-relay check` passes.

## Guardrails

- ViewModel layer only — no changes to `RelayTaskRepository`, the driver, or the drain.
- Do not remove the busy gating from any command other than Refresh.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs; touched files
  stay under the 300-line guard.
