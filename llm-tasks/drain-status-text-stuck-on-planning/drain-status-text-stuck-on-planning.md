# Drain status text stays stuck on "Planning <last task>…" during the execute phase

When you click **Run All**, the queue drain runs in two phases: a concurrent **plan**
phase (stages 1–4 for every task at once) followed by a sequential **execute** phase
(stages 5–11, one task at a time). The bottom-left **status text** is set to
`Planning <taskId>…` for each task as it enters planning — so by the time the plan
phase ends it reads `Planning <last task>…` (whichever task started planning last,
e.g. `Planning move-run-sh-to-root…`).

**The bug:** when the execute phase begins and works through the FIRST task, the
status text is **never updated** — it stays frozen on `Planning <last task>…` for the
whole run, even though the app is actually executing a *different* (earlier) task.
A user reads "Planning move-run-sh-to-root…" while the app is really running
`add-hover-tooltips` at stage 5. The status is corrected only at drain end.

The single-task **Run selected / Resume** path does NOT have this bug — it sets
`Running <taskId>` at start. Only the **Run All** drain path is affected, because its
execute-start hook never touches the status text.

## Current state (researched)

> **Freshness contract:** locate each anchor by searching for the quoted snippet or
> symbol — never by line number. If a quoted snippet no longer exists where
> described, treat this researched state as stale and re-derive it from the current
> code before changing anything.

The drain's UI lifecycle hooks are built in
`src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs`, in
`CreateDrainLifecycleCallbacks()`:

- `OnPlanningStarted` sets the planning status (planning is concurrent, so the LAST
  task to start planning wins this assignment):
  ```csharp
  OnPlanningStarted = taskId =>
  {
      StatusText = $"Planning {taskId}…";
      Tasks.FirstOrDefault(t => t.Id == taskId)?.MarkPlanning();
  },
  ```

- `OnExecuteStarted` begins a task's execution but **never sets `StatusText`** — this
  is the root cause; the stale `Planning <last task>…` survives:
  ```csharp
  OnExecuteStarted = taskId =>
  {
      var task = Tasks.FirstOrDefault(t => t.Id == taskId);
      if (task is not null)
          BeginRunningTask(task);
  },
  ```
  `BeginRunningTask` (same file) updates `_runningTaskIds`, the task rows, and
  run-start state, but does not assign `StatusText`.

- For contrast, the single-run path `RunOneAsync` in
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` DOES set it:
  `StatusText = $"Running {task.Id}";` at start, and `Committed <id>` / `Flagged <id>`
  on completion.

- `OnExecuteCompleted` clears the running task and refreshes the detail-pane error but
  sets no status. After the LAST task, the drain-completion path in
  `MainWindowViewModel.Execution.cs` sets a terminal status (`FormatQueueStatus()`,
  `"Paused at task boundary"`, `"Drain halted: commit gate rejected consecutive
  tasks"`, or the committed/flagged counts) — so the staleness is visible only
  *during* execution and then self-corrects at the end.

- Per-stage progress flows through `UpdateRunningStage(taskId, stageNumber, stageName)`
  (LiveState.cs), which updates the task row + detail context but not `StatusText`.

## What to do

Keep the drain's execute-phase status text in sync with the task actually running —
mirroring the single-run path — so it never shows a stale planning message.

- In `OnExecuteStarted` (or inside `BeginRunningTask`), set
  `StatusText = $"Running {taskId}"` when a task begins executing. (If you put it in
  `BeginRunningTask`, confirm the single-run path's explicit `Running {task.Id}`
  assignment doesn't double-set in a way that regresses its tests — dedupe if so.)
- Strongly preferred: also reflect live stage progress, e.g.
  `Running <taskId> — NN/<stageName>`, by updating the status in
  `UpdateRunningStage`, so the bottom-left tracks the live stage like the stage cards
  do. Match the tone/format of the existing status strings.
- Watch for races: planning is concurrent and asynchronous. A late
  `OnPlanningStarted`/`OnPlanningCompleted` event for one task must NOT overwrite the
  execute-phase status of another. The status should reflect the EXECUTING task once
  execution has started. (Guard with the current `_runningTaskId`, or stop emitting
  planning-status once any execute has begun.)
- Do NOT regress the single-run path or the terminal drain-completion statuses
  (queue drained / paused at boundary / drain halted / N committed / N flagged).

## Acceptance criteria

- During a **Run All** drain, once execution starts the status text reflects the task
  being executed (and ideally its current stage), never a stale
  `Planning <other task>…`.
- Single-task **Run selected / Resume** status behavior is unchanged.
- Terminal drain-end statuses are unchanged.
- A headless test drives the drain lifecycle callbacks and asserts the status text is
  `Running <executing task>` (not the last planning message) after `OnExecuteStarted`.
  `CreateDrainLifecycleCallbacks()` is `internal` precisely so a VM test can drive the
  run-start/completion hooks directly without launching a real swival run — use it.
  Headless UI tests must use `[AvaloniaFact]`/`[AvaloniaTheory]` (see AGENTS.md).
