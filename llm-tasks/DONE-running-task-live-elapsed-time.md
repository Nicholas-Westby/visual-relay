# Show a live, per-second elapsed time for the running task

While a task is running, the UI shows what stage it's on (e.g. the running queue
card reads "Running · Stage 05 · Author-tests") but never how long it has been
running. The only time shown is the task header chip ("2 stages 2m 36s $0.005"),
which is the **accumulated metric of finished stages** from run history, not a
live clock — it doesn't advance while the current stage runs. There should be an
elapsed timer for the running task that ticks every second.

## What's missing (researched)

- **No run-start timestamp is captured.** `BeginRunningTask`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs:15-23`) sets
  `_runningTask`/`_runningTaskId` but records no start time; nothing in the view
  models stores when the run (or current stage) began.
- **No per-second refresh.** The running queue card shows
  `TaskRowViewModel.MetricsLine` → `RunningStepLabel`
  (`src/VisualRelay.App/ViewModels/TaskRowViewModel.cs:40-43`), a static
  "Stage NN · <name>" string. Nothing re-evaluates on a 1s cadence.
- The header time is `TaskRunMetric.SummaryLabel`
  (`src/VisualRelay.Domain/RunMetrics.cs`), computed from completed-stage reports
  in `LoadRunHistoryAsync` — it only updates after stages finish, so it looks
  frozen mid-run.
- The running **stage** card likewise shows "Running · No run yet"
  (`StageBoard.axaml` via `StageRowViewModel`) with no live time.

There is already a precedent for an app-only ticking timer: the backend status
monitor `DispatcherTimer` started from `StartBackendMonitoring`
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs:174-183`), called ONLY
from `App.OnFrameworkInitializationCompleted` so unit tests never spin a timer.

## Recommended fix

1. **Capture a start time when a run begins.** In `BeginRunningTask` (and/or
   `RunOneAsync`, `MainWindowViewModel.Execution.cs:67-72`) record
   `DateTimeOffset.UtcNow` for the running task; reset it in `ClearRunningTask`
   (`LiveState.cs:38-59`). (`DateTimeOffset.UtcNow` is fine in app code.)
2. **Format elapsed with a pure, unit-tested function** — e.g.
   `ElapsedFormatter.Label(TimeSpan)` returning `"7s"`, `"1m 04s"`, `"2m 36s"`,
   mirroring the existing duration formatting in `RunMetrics.cs`
   (`FormatDuration`). Keep it pure so it's testable without any clock/timer
   (pass the elapsed `TimeSpan` in).
3. **Expose a live `RunningElapsedLabel`** on the running `TaskRowViewModel` (and
   surface it on the running queue card next to the stage label, e.g.
   "Stage 05 · Author-tests · 0:42") and/or the task header. Optionally show a
   per-stage elapsed on the running stage card by also capturing the stage start
   when `UpdateRunningStage` fires (`LiveState.cs:25-36`) — call out if you
   include this.
4. **Tick every second via an app-only timer.** Reuse the established pattern:
   start a 1-second `DispatcherTimer` ONLY from `App` (extend
   `StartBackendMonitoring`, or add a sibling started the same way) whose tick
   raises change notification for the running task's elapsed label while a task
   is running. Do NOT start it from the constructor or `LoadInitialAsync`, so
   unit tests (which call `LoadInitialAsync` directly) spin no timer. The tick
   should be a no-op when nothing is running.
5. **Stop/clear cleanly.** When the run ends (`ClearRunningTask`), the live label
   disappears (the card reverts to its run-history metric) — no stale ticking.

## Done when

- A running task shows an elapsed time that visibly advances about once per
  second (queue card, and/or task header), starting from when the run began.
- When the run finishes or is cleared, the live timer stops and the card shows
  the settled run-history metric (no frozen or runaway value).
- The elapsed formatter is a pure, unit-tested function (`"7s"` / `"1m 04s"` /
  `"2m 36s"` boundaries). Write the failing test first.
- The per-second timer is started only from `App` — confirm no timer runs during
  unit tests (which construct the view model and call `LoadInitialAsync`).
- Verify with `./visual-relay screenshot` (note a static screenshot can't show
  ticking; eyeball a real `./visual-relay launch` if possible).
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
