# Make the QUEUE-title clip test self-contained

Follow-up from the code review of `queue-text-cut-off` (commit `7840b99`). Test robustness
only — the production fix is correct.

## Current state (researched)
`tests/VisualRelay.Tests/QueuePanelTitleLayoutTests.cs` opens the window at 1440×900 and
asserts the `TextBlock.panelTitle` has `Bounds.Width >= DesiredSize.Width - 1`. It is a
genuine regression anchor TODAY only because `MainWindow.axaml` pins `QueuePanel` to
`Width="280"`, which squeezes the (pre-fix) `*` title column. If that width ever grows,
the test would silently pass on the broken layout.

## What to build (test only)
Make the test independent of `MainWindow.axaml`'s panel width, so it always fails if the
header column structure regresses:
- Option A (preferred): before `RunJobs()`, constrain the `QueuePanel` (or its host) to a
  provably narrow width (e.g. 200px) that guarantees the old `*`-column layout would clip,
  then assert `Bounds.Width >= DesiredSize.Width - 1` as now.
- Option B: assert the column STRUCTURE directly — the title `TextBlock` is in
  `Grid.Column == 0`, and the parent header `Grid`'s first `ColumnDefinition` is
  `GridLength.Auto` (not star), with a star spacer column present.
- Doing both (visual + structural) is fine.
- `./visual-relay check` green. No production change expected.

## Decisions (settled)
- Test hardening only; do not change `QueuePanel.axaml` production layout.
