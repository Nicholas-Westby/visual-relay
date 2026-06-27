## Stage 1 - Ideate

{
  "summary": "Allow drag-and-drop reordering of tasks during an active run by either (a) restricting reorder to only pending (not-yet-started) tasks, (b) allowing full visual reorder while maintaining a stable execution-order snapshot that only affects future scheduling, or (c) pausing the run on drag-initiation, letting the user reorder, then resuming execution in the new order.",
  "options": [
    "Reorder pending tasks only — running/completed tasks stay fixed in place; only tasks that haven't started yet can be dragged. This avoids any execution-order ambiguity and is simplest to implement. Trade-off: users cannot move a pending task ahead of a currently-running one.",
    "Full visual reorder with a separate execution queue — allow reordering any task visually. The execution order is captured at run-start (or lazily updated); running tasks continue to their natural completion, and the visual reorder only affects which task runs next after the current one finishes. Trade-off: visual order may temporarily diverge from execution order, requiring clear UI affordances (e.g., badges showing 'running', 'queued', 'done').",
    "Pause-then-reorder — when the user grabs a task during an active run, pause the execution, present the full reorder UI, and then resume the run in the new order. Trade-off: most disruptive to the flow, but cleanest semantics and no ambiguity about the execution sequence."
  ]
}

## Stage 2 - Research

{
  "findings": "The feature is blocked by two coordinated gates: (1) `QueuePanel.ReorderEnabled` checks `IsBusy == false` to prevent drag initiation, and (2) `MoveTask` in the ViewModel returns early when `IsBusy`. `IsBusy` is an ObservableProperty set true during `RunBusyAsync`, which wraps both single-run (`RunOneAsync`) and drain (`DrainQueueAsync`) paths. Critically, `RelayQueueController.DrainAsync` takes a local copy of `Tasks` (`var queue = Tasks.ToList()` at line 113) at drain start and executes from that snapshot — so reordering the visible `Tasks` during a drain **cannot** affect the execution order of the in-flight drain. Completed tasks are removed from `Tasks` by ID lookup; flagged tasks are re-appended at the end. After drain completion, `ReloadTaskListAsync` rebuilds the `Tasks` collection from disk via `TaskOrderStore.Apply()`, so any in-memory reorder must be persisted to `.relay/task-order.json` or it will be lost. During single runs, the running task is tracked by `_runningTaskIds`/`BeginRunningTask`/`ClearRunningTask` and the `Tasks` collection is not mutated. The `TaskRowViewModel` already renders running tasks visually (green accent, stage number, elapsed time).",
  "constraints": [
    "IsBusy must remain the gate for starting new runs (CanRunSelected/CanDrain) — only the reorder gate should be decoupled from IsBusy.",
    "Drain's queue snapshot (`var queue = Tasks.ToList()`) determines execution order for the current drain; reordering the visible Tasks doesn't affect in-flight execution.",
    "Post-drain `ReloadTaskListAsync` clears and rebuilds Tasks from disk — any in-memory reorder must be persisted via TaskOrderStore.Save() before the refresh or the reorder is lost.",
    "Flagged tasks are re-appended at the end of Tasks during drain (RelayQueueController line 242) — a reorder moving a task up is partially undone if the task gets flagged.",
    "Completed tasks are removed from Tasks by ID during drain — a reorder of a completed task slot is moot.",
    "MoveTask already calls TaskOrderStore.Save() on every successful move — persistence is already wired.",
    "QueuePanel's drag-and-drop uses ReorderEnabled to gate both drag start (OnListPointerPressed) and drag-over/on-drop (OnDragOver/OnDrop) — all three need relaxation.",
    "The existing test `MoveTask_IsNoOpWhenBusy` (MainWindowViewModelTests.Reorder.cs line 150) asserts current blocking behavior and must be updated.",
    "During Phase 1 (planning) of drain, task removal uses `IndexOf(taskId)` then `RemoveAt(idx)` — safe by ID lookup regardless of reorder.",
    "Running tasks already have distinct visual styling (green accent, border, shadow, stage/elapsed labels) — dragging a running card is purely a UI reorder.",
    "`CanEditSelectedTask` blocks editing the currently-running task specifically (by ID match to `_runningTaskId`), not by IsBusy — a useful pattern for selective gating."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Two coordinated gates block drag-and-drop reordering during active runs: (1) QueuePanel.ReorderEnabled checks `IsBusy == false` to prevent drag initiation and drag-over (QueuePanel.axaml.cs:56-57, checked at lines 61 and 122), and (2) MainWindowViewModel.MoveTask returns early when IsBusy (Commands.cs:115). IsBusy is set true by RunBusyAsync (Helpers.cs:166) for the entire duration of a drain, which can span many tasks and minutes. The drain is safe to reorder during because RelayQueueController.DrainAsync snapshots Tasks at line 113 (`var queue = Tasks.ToList()`) and the controller has its own separate ObservableCollection — the ViewModel's visible Tasks are not consumed by the drain. Post-drain ReloadTaskListAsync (Helpers.cs:115) rebuilds from disk, but MoveTask already persists via TaskOrderStore.Save() on every move (Commands.cs:134). The existing test MoveTask_IsNoOpWhenBusy (MainWindowViewModelTests.Reorder.cs:150) explicitly asserts the current blocking behavior and must be updated. The CanEditSelectedTask pattern (Authoring.cs:36) shows a precedent for selective per-task gating by running-task ID rather than the global IsBusy flag.",
  "excerpts": [
    "QueuePanel.axaml.cs:56-57 — `private bool ReorderEnabled => DataContext is MainWindowViewModel { IsBusy: false, ShowArchive: false };` gates drag start, drag-over, and (implicitly) drop.",
    "QueuePanel.axaml.cs:61 — `if (!ReorderEnabled || …)` aborts drag initiation in OnListPointerPressed.",
    "QueuePanel.axaml.cs:122 — `if (!ReorderEnabled || …)` rejects DragOver with DragDropEffects.None.",
    "Commands.cs:113-118 — `internal void MoveTask(int fromIndex, int toIndex) { if (IsBusy || ShowArchive) { return; } … }` — the VM-level gate.",
    "Helpers.cs:157-177 — `RunBusyAsync` sets `IsBusy = true` for the entire drain duration, blocking reorder for minutes.",
    "RelayQueueController.cs:113 — `var queue = Tasks.ToList();` snapshots the controller's separate Tasks; reordering the ViewModel's Tasks cannot affect drain execution.",
    "Helpers.cs:115-140 — `ReloadTaskListAsync` rebuilds Tasks from disk via `TaskOrderStore.Apply()`, so reorder must persist to survive post-drain reload.",
    "Commands.cs:134 — `new TaskOrderStore(RootPath).Save(Tasks.Select(task => task.Id));` — MoveTask already persists on every move.",
    "MainWindowViewModelTests.Reorder.cs:150-159 — `MoveTask_IsNoOpWhenBusy` test asserts current blocking behavior and must be updated.",
    "Authoring.cs:36-40 — `CanEditSelectedTask` blocks editing only the currently-running task by ID match (not IsBusy), demonstrating selective gating pattern."
  ],
  "repro": "1. Create a repo with 3+ tasks. 2. Click 'Run All' to start a drain. 3. While tasks are running (IsBusy=true), attempt to drag any task to a new position. Expected: drag initiates and task reorders. Actual: drag is completely blocked — pointer press is ignored (QueuePanel line 61), drag cursor never appears, and even if it did, MoveTask returns early at line 115."
}

## Stage 4 - Plan

{
  "plan": "Remove `IsBusy` from the two reorder gates — QueuePanel.ReorderEnabled and MainWindowViewModel.MoveTask — while leaving all other IsBusy guards (CanRunSelected, CanDrain, CanRefresh, CanMarkSelectedTaskDone, CanAddAttachments) intact. The drain already snapshots execution order from the controller's own collection, and MoveTask persists every move via TaskOrderStore.Save(), so reordering during an active run is safe and survives the post-drain reload. Update the MoveTask_IsNoOpWhenBusy test to assert that reorder succeeds when busy.",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/QueuePanel.axaml.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs",
    "tests/VisualRelay.Tests/MainWindowViewModelTests.Reorder.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/MainWindowViewModelTests.Reorder.cs"],
  "rationale": "Renamed MoveTask_IsNoOpWhenBusy to MoveTask_ReordersWhenBusy and inverted its assertion: the test now expects MoveTask(0, 1) to produce [b, a] even when IsBusy=true, and asserts persistence via TaskOrderStore.Read(). The test correctly FAILS because MoveTask still returns early when IsBusy. The other reorder tests (ShowArchive gate, out-of-range gate, persistence-survives-reload) all continue to pass, confirming the change is isolated to the busy-gate removal."
}

## Stage 6 - Implement

{ "summary": "Removed IsBusy gating from QueuePanel.ReorderEnabled and MainWindowViewModel.MoveTask. Drag-and-drop reorder now works during active runs. The drain snapshots execution order from its own collection, and MoveTask persists every move via TaskOrderStore.Save(), so reordering is safe and survives the post-drain reload. All 23 tests pass." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "All three files already contain the correct changes from stage 6: QueuePanel.ReorderEnabled no longer checks IsBusy, MoveTask no longer guards on IsBusy, and MoveTask_ReordersWhenBusy asserts reorder succeeds when busy. All 23 tests pass."}

## Stage 9 - Verify

{
  "summary": "Removed `IsBusy` from both reorder gates (`QueuePanel.ReorderEnabled` and `MainWindowViewModel.MoveTask`) so tasks can be drag-reordered during an active run. The drain snapshots execution order from its own collection at drain-start, and `MoveTask` already persists every reorder via `TaskOrderStore.Save()`, so reordering the visible list during a run is safe — it only affects future scheduling and survives the post-drain `ReloadTaskListAsync` rebuild. Renamed the old `MoveTask_IsNoOpWhenBusy` test to `MoveTask_ReordersWhenBusy` and inverted its assertion. All 10 reorder-related tests pass.",
  "commitMessages": [
    "feat: allow drag-and-drop reorder during active runs",
    "feat: remove IsBusy gate from QueuePanel reorder and MoveTask",
    "feat: enable reordering tasks while drain is in progress"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

