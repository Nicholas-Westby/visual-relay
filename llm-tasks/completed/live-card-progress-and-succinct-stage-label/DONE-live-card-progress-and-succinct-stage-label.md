# Make the queue card's progress live and its running-stage label succinct

Two card defects observed by the maintainer during a real run, both diagnosed in source:

1. **The progress bar only moves on Refresh.** The card binds
   `ProgressFraction => Math.Clamp(Task.CompletedStageCount / 12.0, 0, 1)`
   (src/VisualRelay.App/ViewModels/TaskRowViewModel.cs:103). `CompletedStageCount` comes from
   `RunMetrics` attached when the task LIST is loaded from disk
   (src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:243-253, RunMetrics.CompletedStageCount =
   Stages.Count of the last recorded run) — so during a live run the value is frozen (and on a
   task's FIRST run it is 0 throughout). Stage events during the run call
   `TaskRowViewModel.MarkRunning(...)` but nothing recomputes the fraction, `UpdateTask(...)`'s
   notification list omits `ProgressFraction`, and the `12.0` denominator is a hardcoded stage
   count. Net effect: the bar looks static/absent until a full task-list reload (the Refresh
   button) republishes metrics.
2. **The running-status line grows into an unreadable "Stage 01 & Stage 02 & Stage 03 & …".**
   `MainWindowViewModel.LiveState.cs:157` does `_runningStageNumbers[taskId].Add(stageNumber)` on
   every stage START and never removes entries when a stage completes — the set is "every stage
   that has ever started this run", not "stages running now". `RunningStepLabel`
   (TaskRowViewModel.cs:66-70) then joins the WHOLE set with `" & "` as soon as Count > 1 and
   drops the stage NAMES entirely; the card clips it with `TextTrimming="CharacterEllipsis"`
   (QueuePanel.axaml MetricsLine block). The multi-entry form was presumably meant for the
   genuinely concurrent Review ∥ Visual-review pair (stages 7+8), which is the only legitimate
   concurrency in the pipeline.

Note: `report-review-pair-stages-independently` (landing just before this task) makes each pair
stage publish its own completion — consume those per-stage signals here; do not re-derive pair
state.

## What to build

1. **Live progress.** Track per-running-task completed-stage count in the view-model layer from
   the same stage lifecycle events that drive `UpdateRunningStage`/stage completion, and have the
   card's fraction prefer the LIVE count while the task is running (falling back to the last-run
   `RunMetrics` when idle). Notify `ProgressFraction` on every stage completion (and include it in
   `UpdateTask`'s notification list). Derive the denominator from the actual pipeline stage list
   (`RelayStages`) instead of the literal `12.0`, counting Skipped stages as progressed so the bar
   still reaches full on runs with skipped stages.
2. **Truthful running set.** Remove a stage from `_runningStageNumbers[taskId]` when that stage
   completes (Done/Skipped/Flagged), so the set means "in flight right now". After this, the
   multi-entry case can ONLY be a genuinely concurrent group.
3. **Succinct label.** Redesign `RunningStepLabel`:
   - Single stage (the overwhelmingly common case, unchanged shape): `Stage 07 · Review`.
   - Concurrent stages: one compact segment such as `Stages 07+08 · Review ∥ Visual-review` —
     numbers joined with `+`, names joined with `∥` (or a similarly compact separator), never the
     unbounded `" & "` chain, and never nameless numbers. Completed stages are the progress bar's
     job — the label must never enumerate them.
   - Keep `Starting task` / `Planning…` behaviors as they are.
4. No new bindings/markup complexity in QueuePanel.axaml beyond what the label/fraction changes
   require; MaxLines/trimming stay as a safety net, but the designed content must fit a typical
   card width without relying on it.

## Tests (red first)

- Stage-completion event removes the stage from the running set: drive start(7), start(8),
  complete(8) → label shows `Stage 07 · Review`; complete(7) → label leaves the multi form.
- Concurrent form: start(7)+start(8) → label matches the compact pair format (numbers + names,
  no " & ").
- Live fraction: sequence of stage completions raises `ProgressFraction` monotonically WITHOUT
  any task-list reload, with PropertyChanged raised for `ProgressFraction` each time; idle rows
  fall back to RunMetrics-derived fraction.
- Denominator: fraction uses the RelayStages count (assert full bar when all stages incl. a
  Skipped one are done), not 12.0 (guard against the literal reappearing).
- `UpdateTask` notifies `ProgressFraction`.

## Verification

- `./test.sh` fully green including the new tests.
- Ledger note: with a real (or simulated) run, the card's bar visibly advances at stage
  boundaries with no Refresh click, and the running label never exceeds the pair form.
