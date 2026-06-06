# Tolerate new `llm-tasks/*.md` files appearing mid-run without disrupting the active run

Visual Relay runs one task at a time behind the `.relay/ACTIVE/` lock, but task
files live on disk under `llm-tasks/` and can be written by an author (or another
tool) *while a run is in progress*. Today the queue-drain path that the headless
runner relies on reads the task list from a **live, mutable** collection rather
than a snapshot, so a concurrent refresh can change which task index 0 points at
mid-drain — silently reordering, skipping, or re-running a task. Separately, the
red-gate stash and the stage-11 commit are scoped by git pathspecs, but that
scope is derived from the **LLM-produced stage-4 manifest**, so a manifest that
erroneously lists an `llm-tasks/*.md` path is the one way a stray task file could
be stashed or committed by an unrelated run. This task pins down the run-set at
drain start and proves the run's git scope can never reach into `llm-tasks/`, so
a file added mid-run is inert for the active run and simply shows up for the next
one.

## Current state (researched)

**The drain loop reads a live collection, not a snapshot.**
`RelayQueueController.DrainAsync` loops on `while (Tasks.Count > 0)` and takes the
head with `var task = Tasks[0]` / `Tasks.RemoveAt(0)`
(`src/VisualRelay.Core/Queue/RelayQueueController.cs:72-77`). `Tasks` is the
public `ObservableCollection<RelayTaskItem>`
(`RelayQueueController.cs:22`) and the only refresh path,
`RefreshAsync`, mutates it in place with `Tasks.Clear()` then re-adds every
pending task (`RelayQueueController.cs:25-35`). Because `ListPendingAsync` walks
`llm-tasks/` and returns tasks sorted by id
(`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:45-51`, `Walk` at `:83-118`),
a refresh triggered after a new file lands re-orders the collection. Any refresh
interleaved with a drain therefore changes what `Tasks[0]` resolves to on the
next iteration — the in-flight task can be dropped from results, a different task
can be entered, or a just-finished task can reappear at the head. The drain has
no record of "the set I started with."

The GUI path already avoids this: `DrainQueueAsync` snapshots with
`var queue = Tasks.Where(task => !task.NeedsReview).ToList()` and iterates the
list, removing from both the snapshot and the observable collection
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs:54-71`). The
fix is to give `RelayQueueController.DrainAsync` the same snapshot discipline so
both entry points (GUI and `tools/VisualRelay.RunTask`, which calls the driver
directly via the same `IRelayTaskRunner`) behave identically. There is no
`FileSystemWatcher` in the app, and `CanRefresh()` is gated on `!IsBusy`
(`MainWindowViewModel.Helpers.cs:178`), so the GUI never auto-refreshes mid-run —
the remaining exposure is purely the un-snapshotted controller loop.

**Git scope is pathspec-bounded, but the bound comes from the LLM manifest.**
The red gate stashes only files it computes from the manifest: `StripToRedAsync`
filters to `present` (manifest entries that exist on disk) and runs
`git stash push -u -m <tag> -- <present>`
(`src/VisualRelay.Core/Execution/RedGate.cs:33-48`), so `-u` (untracked) cannot
reach a new `llm-tasks/*.md` *unless that path is in the manifest*. The stage-11
commit is likewise scoped: `GitCommitter.CommitAsync` does `git reset -q` then
`git add -A -- <manifestFilesToStage> <proofFiles>`
(`src/VisualRelay.Core/Execution/GitCommitter.cs:20,35`), where `proofFiles` is a
fixed `.relay/<task>/` triple
(`src/VisualRelay.Core/Execution/RelayDriver.cs:146`) and `manifestFilesToStage`
is resolved from the same manifest
(`GitCommitter.cs:66-89`). The manifest itself is read from stage-4 LLM JSON
(`RelayDriver.cs:81-83`, `ReadStringArray` in
`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs:40-51`) and also feeds
`WorkingTreeHash` (`RelayDriver.Artifacts.cs:58-69`). So the lone path by which a
mid-run task file is swept into a stash or commit is a manifest that names a
`llm-tasks/*.md` entry. Nothing today rejects such an entry; the scope is correct
in practice but unguarded in principle.

The `.relay/ACTIVE/` lock (`src/VisualRelay.Core/Execution/ActiveTaskLock.cs`)
already prevents a *second* concurrent run, and the driver re-reads the specific
task by id at run start (`RelayDriver.cs:34-36`), so the running task's own
identity is stable; the disruption is entirely in (a) the controller's queue
iteration and (b) the unguarded manifest-derived git scope.

## What to build

Two concrete, narrowly-scoped changes plus a guard. Write the failing tests
first.

**1. Snapshot the run-set at drain start in `RelayQueueController`.**
In `DrainAsync` (`src/VisualRelay.Core/Queue/RelayQueueController.cs:64-96`),
capture the run-set once before the loop — e.g.
`var queue = Tasks.Where(t => !t.NeedsReview).ToList();` — and iterate that local
list instead of indexing `Tasks[0]`. Keep the public `Tasks` collection in sync
for the UI by removing each completed task from it (mirror the GUI's
`Tasks.Remove(task)` pattern at `Execution.cs:59-63`), but make the drive order
and termination depend only on the snapshot. The result is: tasks added to
`Tasks` after the drain began are not entered by the current drain, and a
concurrent `RefreshAsync` cannot change which task runs next or duplicate one.
Preserve every current behavior the existing tests assert
(`tests/VisualRelay.Tests/RelayQueueControllerTests.cs`): manual reordering via
`MoveUp`/`MoveDown` taken *before* drain still applies (it changes `Tasks`, hence
the snapshot), pause-at-boundary still leaves the remaining tasks in `Tasks`, and
the circuit-breaker halt still leaves untouched tasks in `Tasks`.

**2. Guard the run's git scope against `llm-tasks/` so the manifest can never
sweep a task file.** Add a single validation point so a stray (or
mistakenly-manifested) task file is impossible to stash or commit. Reject any
manifest entry that resolves under the configured tasks dir: compute the tasks
dir from `RelayConfig.TasksDir` and, where the manifest is finalized in
`RelayDriver.RunTaskAsync` (stage 4, `RelayDriver.cs:81-83`), drop or flag any
entry whose normalized relative path is inside `TasksDir`. Prefer **flag**
(fail the run via `FlagAsync`, `RelayDriver.cs:194`) with a clear reason like
`"manifest may not include task files under <tasksDir>"`, since a manifest that
wants to edit `llm-tasks/` is itself a bug — this keeps the stash
(`RedGate.StripToRedAsync`) and commit (`GitCommitter`) provably free of task
files because they only ever see the filtered manifest plus the fixed
`.relay/<task>/` proof triple. Do not broaden `proofFiles`
(`RelayDriver.cs:146`); it is already `llm-tasks/`-free.

**Files expected to change:** `src/VisualRelay.Core/Queue/RelayQueueController.cs`
(snapshot), `src/VisualRelay.Core/Execution/RelayDriver.cs` (manifest guard,
possibly a small helper in `RelayDriver.Artifacts.cs`), and the tests below. No
GUI change is required — `DrainQueueAsync` already snapshots and the app has no
mid-run auto-refresh — but if a unit gap exists, add coverage that a task added
to `Tasks` mid-drain is not run by the in-flight drain.

**Tests first:**
- `RelayQueueControllerTests`: a drain that, via the recording runner's
  after-run hook, **adds a new task to `controller.Tasks` mid-run**, asserts the
  newly-added task is NOT in `results`/`TasksRun` for this drain, the original
  run order is unchanged, and the new task remains in `controller.Tasks`
  afterward (available to the next `DrainAsync`). Add a companion test that an
  interleaved `RefreshAsync` during the drain does not change the running order
  or duplicate an outcome.
- Driver/manifest test: a stage-4 manifest containing an `llm-tasks/*.md` path
  causes the run to flag (NEEDS-REVIEW) rather than stash/commit that file; a
  normal manifest is unaffected. If a `RedGate`/`GitCommitter`-level assertion is
  cheaper, additionally assert that given a manifest filtered of task files, a
  new untracked `llm-tasks/<other>.md` present on disk is neither in the stash
  nor in the resulting commit.

## Done when

A task file written into `llm-tasks/` while a run/drain is in progress:

- is **not** swept into the stage-5 red-gate stash (`git stash -u` only ever sees
  the task's own, task-file-free manifest);
- is **not** staged or committed by the stage-11 commit (the commit contains only
  the running task's manifest files and its `.relay/<task>/` proof triple);
- does **not** alter the running task's identity, the drain's run order, or which
  task is entered next — the headless `RelayQueueController.DrainAsync` drives a
  snapshot taken at drain start, matching the GUI;
- does **not** crash or abort the drain; the drain finishes its snapshotted set
  and leaves the new task in the queue;
- is correctly available for the **next** run: after the drain (or on the next
  `RefreshAsync`/`ReloadTaskListAsync`) the new task appears as pending and can be
  run.

Plus: the failing tests above are written first and pass; existing
`RelayQueueControllerTests` behaviors (manual order, pause-at-boundary,
circuit-breaker halt, stale-halt-marker clearing) still hold;
`./visual-relay check` green; C#/XAML files stay under 300 lines; Conventional
Commit subjects.
