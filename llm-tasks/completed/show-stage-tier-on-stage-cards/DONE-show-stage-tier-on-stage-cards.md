# Task: Show each stage's model tier on its stage card

The STAGES board gives each stage a number, a name, a status, and a metrics line,
but never says which model tier runs it. Stage 7 Review runs on `frontier`, stage
8 Visual-review on `vision`, stage 1 Ideate on `cheap`, and nothing on the board
says so. That is operationally real: the tier decides which model, which provider,
and which price serves the stage, so retargeting a tier in Settings silently
changes what half of these cards do.

The tier is already on the row view-model. It is simply not rendered.

### Evidence (2026-08-19)

- `src/VisualRelay.App/ViewModels/StageRowViewModel.cs:27` sets `Tier` from
  `RelayStageDefinition.Tier` in the constructor and line 36 exposes it publicly.
- `src/VisualRelay.App/Services/ControlApi.State.cs:60` is the ONLY consumer:
  `/state` reports `tier` per stage. No XAML binds it, so the tier is visible to
  an API client and invisible to the person looking at the window.
- `src/VisualRelay.App/Views/Controls/StageBoard.axaml:44-86` is the card
  `DataTemplate`. Its grid is `RowDefinitions="Auto,Auto,Auto"`: ordinal plus
  name, then `StatusLabel`, then `MetricLabel`. Nothing binds `Tier`.
- `StageRowViewModel.ModelLabel` (`StageRowViewModel.cs:204`) has the same
  problem. It is written by `ApplyMetric` (`StageRowViewModel.Metrics.cs:52`), by
  `MainWindowViewModel.Stages.cs:36`, and by `MainWindowViewModel.StageMetrics.cs:43`,
  it is asserted by `RelayRunHistoryTests.cs:102`, and it is bound by nothing.
- `ModelLabel` is NOT always a concrete model. `RelayRunHistoryTests.cs:102`
  asserts the value `"cheap"` — what gets recorded is whatever the report named,
  which is frequently the tier alias itself. A naive "tier then model" line
  therefore renders `cheap cheap`.
- `StageRunMetric` carries its own `Tier` (see the constructor call at
  `tests/VisualRelay.Tests/StageCardMetricsLayoutTests.cs:39-44`) and
  `ApplyMetric` drops it on the floor. That recorded tier is the one that actually
  ran, and it can differ from the definition's: stage escalation moves a stage
  from `balanced` to `frontier` mid-run (`StageEscalation.DescribeTransition`, see
  `tests/VisualRelay.Tests/RelayEventTests.cs:34`). An escalation is invisible on
  the board today.
- Space is tight and fixed. `Button.stageButton` pins `Width` to 165 px
  (`src/VisualRelay.App/Styles/VisualRelayTheme.axaml:36-45`) and the card sets
  `MinHeight="64"`. `tests/VisualRelay.Tests/StageCardMetricsLayoutTests.cs` exists
  precisely because an earlier line overflowed 165 px and was ellipsized into
  uselessness. Do not reintroduce that defect.
- No file-size blockers: `StageBoard.axaml` is 94 lines, `StageRowViewModel.cs`
  255, `StageRowViewModel.Metrics.cs` 72. All have headroom under the 300-line
  guard, so no split is needed first.

### What to build

1. Add a `TierLabel` property to `StageRowViewModel` returning the tier to
   display: the tier recorded by the last run when one exists, otherwise the
   definition tier captured in the constructor. `ApplyMetric` must start capturing
   `metric.Tier`, and `ClearMetric` must revert to the definition tier. Leave the
   existing `Tier` property itself unchanged — `ControlApi.State.cs` reads it and
   `/state` output must not change.

2. Append the concrete model to that label only when it adds information: when
   `ModelLabel` is non-empty AND differs from the tier being shown. A
   `cheap`-recorded stage therefore still reads `cheap`, while a frontier stage
   that recorded `glm-5.3` shows both. Choose a separator that survives 165 px.

3. Bind `TierLabel` in the `StageBoard.axaml` card template on a new row below the
   metrics line. Muted secondary styling matching the existing metrics `TextBlock`
   (`Foreground="#8E96A3"`, `FontSize="11"`), with `TextWrapping="Wrap"` and no
   `MaxLines` or `TextTrimming` — it must wrap rather than ellipsize, per the
   regression this board already carries a test for.

4. The label must be present on every card at rest, before any run has happened:
   opening a project with no run history must show `cheap` on Ideate and
   `frontier` on Review.

5. Tests:
   - unit tests on `StageRowViewModel` covering four cases — no run (definition
     tier), a run recording the same tier, a run recording an escalated tier, and
     a run recording a concrete model distinct from the tier;
   - a headless UI test in the `StageCardMetricsLayoutTests` style asserting the
     tier text is present and fully visible (not clipped) on a 165 px card for the
     longest realistic tier-plus-model combination.

### Out of scope

- Do not change `Button.stageButton`'s 165 px width or the card `MinHeight`.
- Do not change `/state`'s `tier` field or any other Control API shape.
- Do not colour-code tiers or introduce a chip or badge control; plain muted text.
- Do not touch tier-to-model resolution, the Settings screen, or
  `BackendConfigGenerator`.
