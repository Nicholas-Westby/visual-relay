# Task metric label: "1 steps" and "steps" vs "STAGES"

The task summary chip reads e.g. `1 steps  34s  $0.00` — wrong plural for a count of one —
and uses the word "steps" while the rest of the UI calls these "STAGES" (the stage board
header and stage cards).

Source: `src/VisualRelay.Domain/RunMetrics.cs:44`:

```csharp
public string SummaryLabel => CompletedStageCount == 0
    ? "No run history"
    : $"{CompletedStageCount} steps  {DurationLabel}  {CostLabel}";
```

`SummaryLabel` is shown as `SelectedTaskMetricLabel` (`MainWindowViewModel.Helpers.cs:175`).
The board header text is `STAGES` (`StageBoard.axaml:14`).

## Recommended fix

Pluralize and align the wording to the rest of the UI: render the count as
`{n} stage` / `{n} stages` (singular when `CompletedStageCount == 1`). Keep one canonical
term — "stage(s)" — across the label and the board.

## Done when

- A one-stage run shows "1 stage …"; a multi-stage run shows "N stages …".
- A `RunMetrics`/`TaskRunMetric` unit test covers the singular and plural cases. Write the
  failing test first.
- (Check for and update any other "steps" wording so terminology is consistent.)
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
