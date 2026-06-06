# Show the swival turn count on each stage card

Visual Relay runs each task through 11 stages; every non-driver stage shells out
to the `swival` agent, which works in multiple LLM "turns". The stage cards on the
board already surface per-stage **time**, **cost**, and **model**, but they never
show **how many swival turns** a stage took. Add a per-stage turn count and display
it on the stage card alongside the existing time/cost.

## Current state (researched)

- **A "turn" is an `llm_call` entry in the report's `timeline`.** Each stage's
  swival run writes `.relay/<task>/stage<N>-attempt<M>.report.json`, whose
  `timeline` array mixes `llm_call` and `tool_call` entries. Verified on
  `.relay/author-edit-and-manage-task-attachments/stage10-attempt1.report.json`:
  50 timeline entries → `Counter({'tool_call': 33, 'llm_call': 17})`, i.e. 17
  turns. The cost estimator **already** uses exactly this definition: in
  `RelayCostEstimator.EstimateReport` (`src/VisualRelay.Core/Costs/RelayCostEstimator.cs:61-96`)
  it builds `llmCalls = timeline.EnumerateArray().Where(IsLlmCall)` where
  `IsLlmCall` (`:98-99`) matches `type == "llm_call"`, then iterates per-turn for
  the incremental input and uses `llmCalls.Length` for the output estimate
  (`:77`). The turn count is therefore precisely `llmCalls.Length` — derive it the
  same way so it stays consistent with the cost model.

- **The estimate record carries no turn count.** `RelayCostEstimate`
  (`src/VisualRelay.Core/Costs/RelayCostEstimator.cs:5-13`) has `Model`,
  `CostUsd`, `Priced`, `PromptTokens`, `CachedTokens`, `OutputTokens`,
  `DurationSeconds`, `CacheWriteTokens` — but no turns field. (Note: the record
  lives in `VisualRelay.Core/Costs`, not `VisualRelay.Domain`.)

- **The driver emits per-stage metrics in `stage_done`.** `PublishStageDoneAsync`
  (`src/VisualRelay.Core/Execution/RelayDriver.cs:259-294`) builds a `Data`
  dictionary with `name`, `time`, `cost`, `sessionCost`, and conditionally `model`
  (only when `cost?.Model` is non-empty). The `cost` is a
  `RelayCostEstimate?` produced by `TryEstimateCost`
  (`RelayDriver.cs:71` → `RelayDriver.Artifacts.cs:124`,
  `RelayCostEstimator.EstimateReport`). For the **driver/commit stage** (stage 11,
  `Kind == "driver"`) there is no swival run, `cost` is `null`, and cost falls back
  to `$0.00` (`RelayDriver.cs:270-274`); no `model` key is emitted.

- **The card consumes `stage_done` and also reloads from history.** Two paths feed
  a stage card:
  - Live: `ApplyStageEventMetric`
    (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:255-276`)
    copies `time` → `DurationLabel`, `cost` → `CostLabel`, `model` → `ModelLabel`
    on the `StageRowViewModel`.
  - History reload: `RelayRunHistory` (`src/VisualRelay.Core/Tasks/RelayRunHistory.cs:81-104`)
    calls `EstimateReport` and builds a `StageRunMetric`
    (`src/VisualRelay.Domain/RunMetrics.cs:3-36`) from the estimate's fields;
    `StageRowViewModel.ApplyMetric` (`StageRowViewModel.cs:121-132`) maps it onto
    the card. `SquashAttempts` (`RelayRunHistory.cs:145-161`) **sums** numeric
    fields across re-run attempts of one stage.

- **The card renders one metric line.** `StageRowViewModel.MetricLabel`
  (`src/VisualRelay.App/ViewModels/StageRowViewModel.cs:35`) is
  `CostLabel == "No cost yet" ? DurationLabel : $"{DurationLabel}  {CostLabel}"`,
  bound in `StageBoard.axaml:73-78` (Grid.Row 2). `DurationLabel`/`CostLabel`
  raise `MetricLabel` change notification on set (`StageRowViewModel.cs:85-107`);
  `ClearMetric` (`:134-141`) resets them. There is no turns property today.

## What to build

Single committed direction: derive the turn count from the timeline exactly as the
cost estimator does, thread it `RelayCostEstimate` → `StageRunMetric` /
`stage_done` Data → stage card. Write the failing test first.

1. **Estimator.** Add a `Turns` field to `RelayCostEstimate`
   (`RelayCostEstimator.cs:5-13`) and set it to `llmCalls.Length` in
   `EstimateReport` (`:61-96`). Do **not** introduce a second timeline scan or a
   different turn predicate — reuse the existing `llmCalls`/`IsLlmCall` so the
   count and the cost always agree. Unknown-model / unpriced reports still carry
   the real `Turns` (turns are independent of pricing).

2. **Driver event.** In `PublishStageDoneAsync` (`RelayDriver.cs:259-294`) add a
   `turns` entry to the `Data` dictionary. Mirror the existing `model` handling:
   only emit `turns` when there is a swival cost estimate with turns
   (`cost is not null && cost.Turns > 0`); for the driver/commit stage (`cost`
   null) emit **no** `turns` key, so stage 11 shows no spurious count — exactly
   how it already suppresses `model`.

3. **History path.** Add `Turns` to `StageRunMetric` (`RunMetrics.cs:3-19`),
   populate it from `estimate.Turns` in `RelayRunHistory` (`:87-103`), and **sum**
   it across attempts in `SquashAttempts` (`RelayRunHistory.cs:152-160`) alongside
   the other per-attempt sums (a stage re-run twice with 12 and 9 turns shows 21).

4. **View model + card.** Add a `TurnsLabel` (or integer `Turns` + computed label)
   to `StageRowViewModel`; set it from the `turns` Data key in
   `ApplyStageEventMetric` (`Helpers.cs:255-276`) and from `metric.Turns` in
   `ApplyMetric` (`StageRowViewModel.cs:121-132`); reset it in `ClearMetric`
   (`:134-141`). Surface it in `MetricLabel` (`StageRowViewModel.cs:35`) so the
   card line reads e.g. `1m 04s  $0.012  17t` (compact `Nt`, or "17 turns" — pick
   one and keep it short so it fits the single trimmed line in `StageBoard.axaml`).
   Append turns only when there is a count; when there are no turns (driver stage,
   or "No run yet"), the line stays exactly as today. Raise `MetricLabel` change
   notification when the turns value changes, like `DurationLabel`/`CostLabel` do.
   Keep the existing time/cost/model rendering byte-for-byte unchanged.

## Done when

- [ ] Each completed swival stage card shows its turn count on the metric line,
      next to the existing time/cost (e.g. `1m 04s  $0.012  17t`).
- [ ] The displayed count equals the report's `llm_call` count — covered by a
      **failing-first** fixture-based unit test on the estimator: feed a captured
      `report.json` (e.g. `stage10-attempt1`, which has 17 `llm_call` of 50
      timeline entries) and assert `estimate.Turns == 17`; assert it equals
      `timeline.Count(type == "llm_call")` and stays consistent with the
      `llmCalls` the cost path uses. Add a multi-attempt test asserting
      `SquashAttempts` sums turns across attempts.
- [ ] The driver/commit stage (stage 11, no swival run) shows **no** turn count
      (no `turns` key emitted, label omits it) — not a spurious `0t` — mirroring
      how it already suppresses model/falls back on cost. Covered by a test on the
      `stage_done` Data for a driver stage.
- [ ] Existing time/cost/model display is unchanged for every existing case
      (verify the current `MetricLabel` / `ApplyStageEventMetric` /
      `RelayCostEstimatorTests` assertions still hold).
- [ ] All new/updated tests were written to fail first against current `main`,
      then pass.
- [ ] `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
      Commit subjects.
