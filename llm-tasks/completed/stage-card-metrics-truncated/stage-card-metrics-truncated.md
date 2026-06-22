# Stage card's metrics line is truncated with an ellipsis, hiding cost/turns/test info

In the center **STAGES** board, each pipeline stage renders as a card showing the number + name
(e.g. "09 Verify"), a status word ("Complete" / "Running"), and a metrics line. On a completed
card the metrics line reads e.g. `17s  $0.0029  4t  test 7s` — but it is **truncated with an
ellipsis** (`17s  $0.0029  4t  test 7…`), so the tail is cut off and there is **no way to read
the full text**. The user noticed it on the Verify card, but this is a **general stage-card
layout problem**: any card whose metrics are long enough (cost + turns + test duration, or a
multi-minute duration) hits the same clip. The information disappears behind an unrecoverable
ellipsis.

## Facts established by reading the code (bake in — do not re-derive)

- **Each stage card is a fixed 165 px wide button.** `src/VisualRelay.App/Views/Controls/StageBoard.axaml`
  (the card `DataTemplate`): `<Button Classes="stageButton" Width="165" …>` (around `:48-49`). The
  cards live in a `WrapPanel` (`:41-44`) inside a `ScrollViewer` with
  `HorizontalScrollBarVisibility="Disabled"` (`:36-39`), so the card width does not grow with the
  window — it is intentionally fixed so cards wrap into columns. **Do not "solve" this by widening
  the window or re-enabling horizontal scroll.**
- **The metrics `TextBlock` clips by design.** Same file, the third row of the card's inner
  `Grid RowDefinitions="Auto,Auto,Auto"` (`:59`):

  ```xml
  <TextBlock Grid.Row="2"
             Text="{Binding MetricLabel}"
             FontSize="11"
             Foreground="#8E96A3"
             MaxLines="1"
             TextTrimming="CharacterEllipsis"/>   <!-- ~:83-88 -->
  ```

  `MaxLines="1"` + `TextTrimming="CharacterEllipsis"` on a 165 px card is exactly what produces
  the `… test 7…` ellipsis. The sibling status row (`:76-82`, bound to `StatusLabel`) and the name
  (`:68-74`, bound to `Name`) trim the same way, but **the metrics line is the one that loses
  information** — a duration/cost/turns/test string is not self-evident once cut.
- **The metric string is assembled in the view model.** `src/VisualRelay.App/ViewModels/StageRowViewModel.cs:45-47`:

  ```csharp
  public string MetricLabel => (CostLabel == "No cost yet" ? LeadingDurationToken : $"{LeadingDurationToken}  {CostLabel}")
      + (string.IsNullOrEmpty(TurnsLabel) ? string.Empty : $"  {TurnsLabel}")
      + (string.IsNullOrEmpty(TestDurationLabel) ? string.Empty : $"  test {TestDurationLabel}");
  ```

  So a completed Verify card concatenates duration + cost (`$0.0029`) + turns (`4t`) + test
  (`test 7s`) onto **one** line. `LeadingDurationToken` (`:43-44`) is the live elapsed while
  Running, else the final `DurationLabel`.
- **The status label is where the "Completed in 17s" option lives.** `StageRowViewModel.cs:37`:
  `public string StatusLabel => Status == "Done" ? "Complete" : Status;`. Moving the duration onto
  this row (e.g. "Completed in 17s") frees the metrics line of its leading duration token, leaving
  cost + turns + test to fit.
- **Not a font / DPI / data issue.** It reproduces from the static card width + single-line
  trimming alone; any sufficiently long `MetricLabel` is clipped at every supported window size.

## Prior art (read for context; do not regress)

- `llm-tasks/DONE-stage-grid-fixed-columns.md` — replaced a fixed 4-column `UniformGrid` with the
  current `WrapPanel`; the 165 px card width is the deliberate outcome. Keep cards wrapping.
- `llm-tasks/DONE-stage-ordinal-inline-with-name.md` — collapsed the ordinal onto the name row and
  set the card to three rows (`Auto,Auto,Auto`) / `MinHeight`; that compact layout is the baseline.
- `llm-tasks/DONE-show-swival-turns-per-stage.md`, `surface-per-step-test-duration`,
  `DONE-task-metric-step-pluralization.md` — these are *why* the metrics line grew (turns "4t" and
  "test 7s" were appended). Do not drop any of those tokens; make them all readable.
- `llm-tasks/DONE-status-chip-truncates-review.md`, `completed/init-panel-buttons-labels-clipped`,
  `queue-text-cut-off` — the same "fixed width + trimming hides text" family elsewhere in the UI;
  follow their resolution style (let content be readable; trimming only as a last-resort fallback).

## Goal

The **full** stage metrics are always readable on every stage card — no duration/cost/turns/test
token hidden behind an unrecoverable ellipsis — at the supported window sizes, robust to the
longest realistic metrics (multi-minute duration + cost + turns + test). The fix must apply to
**all** stage cards, not just Verify. Cards still wrap (no horizontal scroll), and the board stays
visually compact; no change to what the metrics *mean* or to stage-selection behaviour.

## Approach (suggested — Plan/Implement may pick the best, but full readability is mandatory)

The implementer chooses the approach; the hard requirement is that no metric is lost to an
ellipsis. Options, recommended first:

- **(Recommended) Move the duration onto the status row, e.g. "Completed in 17s".** Render the
  finished status as `Completed in {DurationLabel}` (and "Complete" with no duration when none is
  recorded) on the `StatusLabel` row, then drop the leading duration token from `MetricLabel` so
  the metrics line carries only cost + turns + test (`$0.0029  4t  test 7s`) and fits the 165 px
  card. This reads naturally, reuses the existing status row, and keeps the card compact. Keep the
  Running state showing live elapsed (today via `LeadingDurationToken`) — e.g. surface the live
  elapsed on the status row while Running so the card still visibly ticks. Add `OnPropertyChanged`
  for `StatusLabel` wherever duration/elapsed change if the duration moves onto it.
- **Alternatives (any that guarantee full readability):**
  - Let the metrics `TextBlock` **wrap** instead of trimming (`TextWrapping="Wrap"`, drop
    `MaxLines="1"`/`TextTrimming`), accepting a slightly taller card.
  - **Split the metrics across two lines** (e.g. duration + cost on one, turns + test on the next).
  - **Drop `TextTrimming`** on the metrics line and give the card enough height/width to show it.
  - Add a **`ToolTip.Tip="{Binding MetricLabel}"`** so the full text is recoverable on hover. This
    is acceptable only as a *complement*; a tooltip alone is weak (not visible at a glance, awkward
    on touch) — prefer it layered on top of one of the above, not as the sole fix.

Whatever is chosen, the name row may keep trimming (a long stage *name* losing its tail is far less
harmful than a metric), but the **metrics** must be fully visible.

## Files

- `src/VisualRelay.App/Views/Controls/StageBoard.axaml` — the metrics `TextBlock` (and, for the
  recommended option, the status `TextBlock` / card row layout / `MinHeight`).
- `src/VisualRelay.App/ViewModels/StageRowViewModel.cs` — `MetricLabel` (`:45-47`) and, for the
  recommended option, `StatusLabel` (`:37`) plus the relevant `OnPropertyChanged` notifications
  (the `Status`/`DurationLabel`/`ElapsedLabel` setters at `:67-108`, `:150-160`).
- `tests/VisualRelay.Tests/` — new headless layout test + view-model assertions (see Tests).

## Tests (TDD — write the failing test first)

Headless `[AvaloniaFact]`, in the style of `tests/VisualRelay.Tests/InitPanelButtonsLayoutTests.cs`
and `QueuePanelTitleLayoutTests.cs` (show the real `MainWindow`, `Dispatcher.UIThread.RunJobs()`,
resolve the control via `GetVisualDescendants().OfType<StageBoard>()`). `StageBoard` is hosted
directly in `MainWindow.axaml` (`<controls:StageBoard Grid.Row="1"/>`). For a stage with **long**
metrics:

- **Drive a stage into a long-metrics completed state** — set its `Status`/`DurationLabel`/
  `CostLabel`/`TurnsLabel`/`TestDurationLabel` so `MetricLabel` is long (the existing
  `StageRowViewModel { DurationLabel = …, CostLabel = …, TestDurationLabel = … }` pattern from
  `tests/VisualRelay.Tests/TestDurationTests.cs:64-73` shows how; `StageRunMetric` + `ApplyMetric`
  is the alternative, see `TestDurationTests.cs:235-241`). Use a multi-minute duration to stress it.
- **Assert the full metrics are visible, not trimmed.** Resolve the metrics `TextBlock` (the one
  bound to `MetricLabel`) in that card and assert one of, matching the chosen approach:
  - it has **no** `TextTrimming="CharacterEllipsis"` (i.e. `TextTrimming.None`), **or**
  - its arranged `Bounds.Width` ≥ its content's desired width (measure with `Size.Infinity` like
    `AssertButtonWidthSufficient` in `InitPanelButtonsLayoutTests.cs`), so nothing is clipped, **or**
  - if wrapped/split, that every token (`DurationLabel`, `CostLabel`, `TurnsLabel`, the `test …`
    value) is present across the visible metric `TextBlock`(s) and none ends in `…`.

  This must **fail on today's** `MaxLines="1" TextTrimming="CharacterEllipsis"` 165 px card and pass
  after the fix.
- **View-model assertions** (plain `[Fact]`, no UI) for the chosen logic. If the recommended option
  is taken: `StatusLabel` for a `Done` stage with a duration contains "Completed in 17s" (and a
  Done stage with no duration still reads "Complete"); `MetricLabel` no longer leads with the
  duration token yet still contains cost + `4t` + `test 7s`. Reuse / extend the existing
  `StageRowViewModelTests.cs` and `TestDurationTests.cs` cases (e.g. the `MetricLabel` tests at
  `TestDurationTests.cs:63-85`) so their intent is preserved, not broken.

## Sequencing

- One self-contained change. **It must fix all stage cards** — the change is in the shared card
  `DataTemplate` / `StageRowViewModel`, so Verify is just the example; verify a non-Verify long
  card (e.g. a multi-minute stage) is readable too.
- Write the failing headless test first, then the layout/view-model change, then green.
- Keep cards wrapping (no horizontal scrollbar) and the board compact; do not regress the prior-art
  tasks listed above (fixed-width wrapping cards, inline ordinal, turns/test tokens).

## Done when

- [ ] On every completed stage card, the full metrics (duration, cost, turns, test) are readable —
      no token hidden behind an ellipsis — at supported window sizes, including the longest realistic
      metrics on a non-Verify card.
- [ ] Running cards still show the live ticking elapsed; selection/visuals unchanged.
- [ ] Cards still wrap into columns (no horizontal scroll) and the STAGES board stays compact.
- [ ] A failing-first headless `[AvaloniaFact]` asserts the full metrics are present/visible and not
      trimmed for a long-metrics stage; view-model unit tests cover the chosen label logic; prior
      stage-card / metric tests stay green.
- [ ] `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.
