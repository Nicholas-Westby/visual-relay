# Persist manual task order — Run All / Refresh resets the queue to alphabetical

## Background

The task queue (left panel) supports **drag-and-drop reordering** so the user can
prioritize which tasks run first. But after manually reordering and clicking
**Run All**, the visible order snaps back to **alphabetical** — the manual order
is silently lost. Observed: user drags tasks into a custom order, clicks Run All,
and the queue redisplays alphabetically (e.g. `add-hover-tooltips`,
`empty-state-add-arrow`, `fix-discovery-norway-empty`, `move-run-sh-to-root`).

The manual order should **stick** — through Run All, Refresh, and app restart —
and tasks should **run** in that order.

## Current state (researched)

- The queue is listed **alphabetically by task id**:
  `RelayTaskRepository.ListAsync` — `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:43`
  does `.OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase)`. This is the
  source of the alphabetical reset.
- Drag-and-drop is handled in `src/VisualRelay.App/Views/Controls/QueuePanel.axaml.cs`
  (pointer gesture) plus reorder logic on `MainWindowViewModel`; it mutates the
  in-memory `Tasks` `ObservableCollection` but the order is **not persisted**
  anywhere on disk.
- Any list reload re-sorts alphabetically and discards the manual order. Reload
  happens via `ReloadTaskListAsync` (`MainWindowViewModel.Commands.cs:29` for
  Refresh, and others) and `RefreshTasksAfterDrainAsync`
  (`MainWindowViewModel.Execution.cs:126`, run AFTER a drain) — both go through
  `ListAsync` → alphabetical. So **Run All** (reloads after draining) and
  **Refresh** both reset the displayed order.
- The drain has a rank-based execution order
  (`RelayQueueController.cs:81-85`: a `rank` dict + `OrderBy(key).ThenBy(orig)`),
  but it is seeded from the in-memory `Tasks` order, which the surrounding reload
  has already re-alphabetized — so the effective order is alphabetical regardless.

## What to build

Persist the manual order per repo and make BOTH the list load and the drain
respect it instead of re-sorting alphabetically:

1. **Persist** the manual order per repo, e.g. `.relay/task-order.json` mapping
   task id → rank (or an ordered id list), written whenever the user drag-drops.
   It must survive Refresh, Run All, and app restart. (Place it under `.relay/`,
   which is VR's runtime/state dir.)
2. **Order by saved rank first, alphabetical fallback.** Change the queue
   ordering (in `ListAsync` or, better, in the layer that builds the visible
   `Tasks` so the repository stays a pure lister) to sort by persisted rank, then
   fall back to alphabetical for tasks with no saved rank (new tasks). Do not
   hard-code alphabetical as the only order.
3. **Drag-drop updates the persisted order**; adding/removing/renaming tasks
   reconciles gracefully (unranked tasks land at the end or alphabetically among
   the unranked; stale ids are dropped).
4. **The drain consumes the same persisted order** so tasks RUN in the user's
   order, matching the display (keep `RelayQueueController`'s rank logic but seed
   it from the persisted order rather than a re-alphabetized list).

Keep it general (no per-repo/toolchain specifics). Respect the 300-line file
guard; headless UI tests use `[AvaloniaFact]`.

## Tests

- Reorder the in-memory `Tasks`, persist, then reload → the order is preserved
  (not alphabetical).
- After a simulated drain + `RefreshTasksAfterDrainAsync`, the manual order
  survives.
- A newly-created task with no saved rank appears in a sensible place without
  disturbing existing manual ranks.
- Order persists across a simulated restart (reload purely from the persisted
  file).

## Done when

- [ ] After a drag-drop reorder + Run All, the queue stays in the manual order
  (not alphabetical), and tasks run in that order.
- [ ] The manual order persists across Refresh and an app restart.
- [ ] A newly-created task appears without disturbing existing manual ranks.
- [ ] `./visual-relay check` is green (including the new tests).

Conventional Commit subject: `fix(app): persist manual task order so Run All/Refresh don't reset to alphabetical`
