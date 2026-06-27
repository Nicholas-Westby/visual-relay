# Failed stage renders as green "Complete"

When a stage errors, the stage board still paints it green "Complete". Reproduction in the
current sample: `add-multiply` failed in stage 1 — `.relay/add-multiply/stage1-attempt1.report.json`
has `"result": { "outcome": "error", "exit_code": 1 }` and `.relay/add-multiply/NEEDS-REVIEW`
reads `swival exit 1: ... stage 1`. The queue card correctly shows "Needs review", but Stage
01 "Ideate" on the board shows "Complete" and stages 02+ show "Waiting", so nothing on the
board reveals where the run actually failed.

Cause: stage status from history ignores the report outcome.

- `src/VisualRelay.Core/Tasks/RelayRunHistory.cs:71` (`ReadStageMetric`) reads the report
  but never captures `result.outcome` / `result.exit_code`.
- `src/VisualRelay.Domain/RunMetrics.cs:3` (`StageRunMetric`) has no failure flag.
- `src/VisualRelay.App/ViewModels/StageRowViewModel.cs:115` (`ApplyMetric`) flips any stage
  that has a report from `Waiting` to `Done`, regardless of outcome.

A red `"Flagged"` status already exists (`StageRowViewModel.cs:12,36`) but is only ever set
by a live `flagged` event, never from history.

## Recommended fix

Capture the report's failure outcome in `StageRunMetric` (e.g. a `Succeeded`/`Failed` bool
derived from `result.outcome != "ok"` or `exit_code != 0`) in `ReadStageMetric`, and have
`StageRowViewModel.ApplyMetric` set `Status = "Flagged"` for a failed stage instead of
`"Done"`. The errored stage then renders red, matching the task's needs-review state.

## Done when

- Loading a task whose latest stage report errored shows that stage as "Flagged" (red), not
  "Complete"; a clean stage still shows "Complete".
- A failed stage is not counted as a clean completion (see also the step-count label).
- Unit test over `RelayRunHistory` + `StageRowViewModel` covering an errored report drives
  a "Flagged" status. Write the failing test first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
