# Ensure Pause Button Pauses After Active Task

When "Pause after task" is clicked during an in-flight **Run All** (drain), the drain must
stop after the currently-running task completes and enter the `Paused` state — not run the
rest of the queue. Today the click only flips a ViewModel flag; the running
`RelayQueueController` is never told, so the drain runs to completion.

## Current state (researched)

- `MainWindowViewModel.Commands.cs` → `TogglePause()` (`[RelayCommand]`, no `CanExecute`):
  only `PauseRequested = !PauseRequested;` plus `StatusText`. It holds no reference to the
  in-flight drain controller.
- `MainWindowViewModel.Execution.cs` → `DrainQueueAsync()` builds the controller as a
  **local**: `var controller = new RelayQueueController(RootPath, new GuiTaskRunner(...), ...);`
  then `await controller.DrainAsync(mode: SelectedRunAllMode);`. The controller never escapes
  this method.
- The "wire pause" block just before the drain, `if (PauseRequested) controller.RequestPause();`,
  is **dead code**: `CanDrain()` is `!IsBusy && !PauseRequested && ...`
  (`MainWindowViewModel.Helpers.cs`) and `DrainQueueAsync` early-returns when `PauseRequested`
  is true, so `PauseRequested` is always `false` there. Even if it fired,
  `RelayQueueController.DrainAsync` resets `_pauseRequested = false` at its start.
- The controller already pauses correctly *if asked*: `RequestPause()` sets `_pauseRequested =
  true` (and `State = PauseRequested` when `Running`); the Phase 2 loop checks
  `if (_pauseRequested) { State = RelayQueueState.Paused; return results; }` **before**
  dequeuing each next task, so the active task finishes and the next never starts. Proven by
  `RelayQueueControllerTests.DrainAsync_UsesManualOrderAndPausesAtTaskBoundary`
  (`runner.AfterRun = controller.RequestPause;`).
- Conclusion: the only missing piece is the live ViewModel→controller bridge during a drain.

## What to build (TDD-first)

1. **Test first** — add `MainWindowViewModelTests.TogglePauseCommand_PausesActiveDrainAfterCurrentTask`
   (plain `[Fact]`, mirroring the existing controller test): build a `RelayQueueController`
   over a `TestRepository` with three tasks (`alpha`/`beta`/`gamma`) and a `RecordingTaskRunner`
   (`tests/VisualRelay.Tests/TestDoubles.cs`); set
   `runner.AfterRun = () => vm.TogglePauseCommand.Execute(null);` (simulates the user clicking
   Pause after the first task completes); install the controller as the VM's active drain
   controller via the internal seam below; `await controller.DrainAsync()`; assert
   `runner.TasksRun` is `["alpha"]`, `controller.State == RelayQueueState.Paused`, and
   `vm.PauseRequested` is `true`.
2. **Add the bridge field** — `private RelayQueueController? _activeDrainController;` in the
   `MainWindowViewModel.Execution.cs` partial (it has headroom; `MainWindowViewModel.Commands.cs`
   is already at the 300-line guard limit). In `DrainQueueAsync`, assign
   `_activeDrainController = controller;` after construction and clear it in a `try/finally`
   around `await controller.DrainAsync(...)`.
3. **Expose an internal test seam** —
   `internal void SetActiveDrainControllerForTests(RelayQueueController? c) => _activeDrainController = c;`
   (mirrors the existing `RestoreRunningTaskState` test hook). Production sets the field in
   `DrainQueueAsync`; only tests use the setter.
4. **Wire `TogglePause()`** — when arming pause (the new value of `PauseRequested` is `true`)
   and a drain is in flight, call the controller's `RequestPause()`. Add one statement to
   `TogglePause`, e.g. `if (PauseRequested) _activeDrainController?.RequestPause();` — keep the
   edit to that single added line so `MainWindowViewModel.Commands.cs` stays within the
   file-size guard. The existing "Pause armed: finishing … current task before stopping"
   status copy already describes this behavior.
5. **Remove the dead pre-drain wiring** — delete the `if (PauseRequested) controller.RequestPause();`
   block in `DrainQueueAsync`; it can never fire and is misleading.

## Done when

- Clicking "Pause after task" during a Run All stops the drain after the currently-running
  task: `controller.State == RelayQueueState.Paused`, only tasks that already started have run,
  and `vm.PauseRequested == true`. The new VM test passes.
- Pre-arming pause (clicking Pause before Run All) is unchanged — the Run All button stays
  disabled via `CanDrain()` and the early return in `DrainQueueAsync`.
- Existing pause tests still pass: `RelayQueueControllerTests.DrainAsync_UsesManualOrderAndPausesAtTaskBoundary`,
  `RelayQueueControllerParallelTests.DrainAsync_PauseRequested_DuringPlanning_StopsBeforePhase2`,
  `MainWindowViewModelTests.TogglePauseCommand_ShowsTaskBoundarySemanticsAndBlocksNewRuns`.
- `./visual-relay check` is green (file-size guard, format verification, build, tests).

## Guardrails

- Conventional Commit subject (lowercase after prefix, ≤72 chars, no em dash, no trailing
  period, ≤3 `- ` body bullets ≤20 words each); commit directly on `main` — no branches/PRs.
- Keep C# files under 300 lines (`tools/VisualRelay.Guards`); `MainWindowViewModel.Commands.cs`
  is at the limit, so house new fields/helpers in `MainWindowViewModel.Execution.cs`.
- Out of scope: toggling pause **off** (Resume) during an in-flight drain does not cancel an
  armed pause (there is no `CancelPause()` API); the drain still stops at the next boundary,
  after which Resume starts a fresh drain. Leave as-is.
