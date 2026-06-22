# Empty-state "Initialize this project" card: the two action-button labels are clipped

When a folder has no `.relay/config.json`, the QueuePanel shows the **"Initialize this project"**
empty-state card. Its two side-by-side buttons have their labels cut off: **"Create config"**
renders as **"Create conf"** and **"Find it for me"** renders as **"Find it for m"**.

See `init-panel-cropped.png` (the card close up) and `full-window.png` (the whole app).

> Scope note: this task is **only** the label truncation — a pure layout bug, verifiable in the
> headless test harness. The separate problem that the **"Set up empty project"** button does
> nothing when clicked in the live app is a different, live-only defect tracked in
> `set-up-empty-project-button-dead-on-click` — do not fold it in here.

## Facts established by testing (bake in — do not re-derive)

- **The QueuePanel is a *fixed* 280 px wide** (`src/VisualRelay.App/Views/MainWindow.axaml`:
  `<controls:QueuePanel Width="280" …/>`). Measured in a headless render, the card's inner content
  width is ~**212 px** (the `InitEmptyState` Border is `Margin="16"` + `Padding="16"`). This width
  is intentional — do **not** widen the panel to "solve" the clipping.
- **The squeeze is the `*,*` two-column button grid.** The two buttons share
  `ColumnDefinitions="*,*"` (≈102 px per column, measured). With each button's `Padding="12,6"`,
  the labels need more than the column gives, so the text overflows and the panel's outer
  `<Border Classes="panel" ClipToBounds="True">` clips it. Avalonia `Button` does not trim or wrap
  its content by default. Confirmed: in a headless render the `CreateConfigButton` arranges to a
  102 px-wide cell while "Create config" needs more.
- **It is not font/encoding/DPI.** Reproduced deterministically in the headless harness at 1100×640,
  1440×900 and 1920×960 — the panel is fixed-width so window size doesn't change it.

## Current code (verify by searching for the quoted strings, not by line number)

> **Freshness contract.** `QueuePanel.axaml` is edited by several tasks. Locate the card by
> searching for `x:Name="InitEmptyState"`, then adapt. If the snippet has drifted, re-read it.

`src/VisualRelay.App/Views/Controls/QueuePanel.axaml`, inside `<Border x:Name="InitEmptyState" …>`:

```xml
<Grid ColumnDefinitions="*,*" ColumnSpacing="8">
  <Button x:Name="CreateConfigButton" Grid.Column="0"
          Command="{Binding CreateConfigCommand}" Classes="primary"
          Padding="12,6" HorizontalAlignment="Stretch" Content="Create config"/>
  <Button Grid.Column="1"
          Command="{Binding FindTestCommandCommand}"
          Padding="12,6" HorizontalAlignment="Stretch" Content="Find it for me"
          ToolTip.Tip="Ask the frontier model to infer the test command (requires the model backend)"/>
</Grid>
```

## Goal

At the panel's fixed 280 px width, **no action-button label is truncated** — robust to longer /
localized labels, not tuned to today's exact strings. No behaviour change to either command.

## Recommended approach (Plan/Implement may refine)

**Stack the two buttons vertically**, each `HorizontalAlignment="Stretch"` (full ~212 px), instead
of the `*,*` two-column grid. Full width clears the labels with margin to spare and matches the
already-full-width "Set up empty project" button below. Vertical room is ample (the card sits in the
QueuePanel's `*` row). **Preserve** `x:Name="CreateConfigButton"` (an existing UI test resolves it)
and keep the `Classes="primary"` on Create config and the "Find it for me" tooltip.

Rejected alternatives (do not re-litigate): shrinking font/padding to keep them side-by-side
(fragile, still clips on longer labels); widening the panel (280 px is intentional).

## Tests (TDD — write the failing test first)

Headless, in the style of `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs` (the panel is
fixed-width regardless of window size). With the empty state shown (`NeedsInitialization == true`):

- Assert each action button is wide enough for its label — e.g. its arranged `Bounds.Width` ≥ its
  content's desired width — or assert the two buttons are laid out in a single column (equal X,
  increasing Y). This fails on today's `*,*` layout and passes once stacked.
- Keep `ConfigInitEmptyStateUiTests` green.

## Out of scope

- The "Set up empty project" dead-click (`set-up-empty-project-button-dead-on-click`).
- The footer status line, the task `ListBox`, the `HasConfigDiagnostic` border.

## Screenshots

- `init-panel-cropped.png` — the card; "Create conf" / "Find it for m".
- `full-window.png` — the whole app at the real 280 px panel width.
