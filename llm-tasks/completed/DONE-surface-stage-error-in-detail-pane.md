# A failed run shows no error in the main reading pane

The center TASK pane is where you actually read a task, but it only has `Markdown` and `Context`
tabs (`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:70-94`). When the selected
task's latest run errored, nothing in that pane says what went wrong — the reason lives only in
the tiny queue card / run-log chips. The single richest field, the report's
`result.error_message`, is never even read into the view model.

Two gaps:

- **The error message is discarded on read.** `RelayRunHistory.ReadStageMetric`
  (`src/VisualRelay.Core/Tasks/RelayRunHistory.cs:71`) computes success via
  `ReadStageSucceeded` (`:103`, parses `result.outcome` / `result.exit_code`) but never
  captures `result.error_message`. `StageRunMetric` (`src/VisualRelay.Domain/RunMetrics.cs:3`)
  has no field to hold it.
- **The detail pane has nowhere to show it.** The header sets `SelectedTaskMarkdown` /
  `SelectedTaskContext` (`MainWindowViewModel.Commands.cs:206-217`) and the metric label
  (`MainWindowViewModel.Helpers.cs:189`) but carries no failure text.

## Recommended fix

Thread the report's error message through and surface it in the detail pane when the selected
task's latest stage failed:

1. Add a nullable `ErrorMessage` to `StageRunMetric` and populate it in `ReadStageMetric` by
   reading `result.error_message` (reuse the JSON read already in `ReadStageSucceeded` so the
   report is parsed once and both `Succeeded` and `ErrorMessage` come out together).
2. Expose the failed stage's message on the view model (e.g. `SelectedTaskError`, set alongside
   the metric label in `Helpers.cs`; null when the latest run is clean).
3. In `TaskDetailPanel.axaml`, when `SelectedTaskError` is non-empty, show it prominently —
   either a red banner above the `TabControl` or a third "Error" tab — using the flagged red
   already defined (`StageRowViewModel.cs:12`, `#F36F63`). Full text, wrapping, selectable.

A clean run shows no error surface; only a failed latest run does.

## Sequencing

- **Coordinate with `attempt-number-hardcoded-overwrites-reruns.md`** — both modify
  `RelayRunHistory` (this task adds an `ErrorMessage` field + reads `result.error_message` in
  `ReadStageMetric`; that task changes attempt selection/ordering and `SquashAttempts`). Land one
  first and rebase the other rather than editing the same file in parallel.
- The `ErrorMessage` this exposes is the same text `error-message-resolution-hints.md` enriches
  and `error-reason-truncated-in-ui.md` reveals. If those have landed, show the hint-enriched
  message here; if not, this surface picks it up automatically once they do.

## Done when

- Selecting a task whose latest run errored shows the full `result.error_message` in the main
  TASK pane (wrapping, selectable), styled as a failure.
- A task with a clean (or no) run shows no error surface and the pane looks as it does today.
- Unit coverage: `RelayRunHistory` reads `error_message` from an errored report into
  `StageRunMetric`; a clean report yields a null/empty error. Write the failing test first.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
