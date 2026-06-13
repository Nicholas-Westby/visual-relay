# Restyle status tags as tags (not buttons) and fix the "Initialize this project" card overflow

Two visual rough spots in the main window:

1. The small count/status pills — "No run history" and "0 pending" in the TASK header, the
   "0" beside "New"/"Archive" in the queue header, the stage/trace counts, the run-log scope —
   are styled almost identically to buttons and their text sits **off-center vertically**, so
   they read as broken buttons rather than labels/tags.
2. The "Initialize this project" card **overflows**: its two action buttons run past the card's
   right edge, and the test-command field's placeholder is clipped mid-word
   ("e.g. dotnet test · pyte").

> **Related:** `04-collapsible-panels-and-task-focus-mode` adds collapsed-rail labels that
> reuse the `Border.chip` tag style refined here — if it is already implemented, keep its
> rail labels consistent with the tag styling defined in this task.

## Current state (researched)

### Tags look like buttons and sit off-center
- Chips use the shared `Border.chip` style (`src/VisualRelay.App/Styles/VisualRelayTheme.axaml:90-96`):
  Background `#1B2130`, 1px border `#303A4C`, `CornerRadius="8"`, `Padding="9,4"` — visually
  nearly identical to the base `Button` (`VisualRelayTheme.axaml:2-9`: `CornerRadius="8"`, a
  border, `MinHeight="36"`).
- In horizontal headers a chip `Border` has no `VerticalAlignment`, so it stretches to the
  row height set by the taller buttons beside it (28–36px) while its `TextBlock` renders
  **top-aligned** → the "odd vertical alignment." Instances:
  - TASK header metric + status chips — `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:22-38`
    ("No run history" / "0 pending", next to the 36px `Run selected`/`Resume` buttons).
  - QUEUE count "0" — `QueuePanel.axaml:18-24` (next to `New`/`Archive`).
  - STAGES count — `StageBoard.axaml:17-23`; RUN LOG scope + LLM COMMANDS count —
    `ActivityColumn.axaml:29-34,79-85`; pause notice — `TopBar.axaml:118-127`.
  - None of these override `VerticalAlignment`, so a style-level default is safe to add.

### Initialize card overflows the 280px column
- The card is `QueuePanel.axaml:151-185`, in the fixed 280px queue column; its `Border` has
  `Margin="16"` + `Padding="16"` → ~216px of content width.
- The action row is a horizontal `StackPanel` (`QueuePanel.axaml:173-183`) holding
  "Create config" + "Find it for me" (each `Padding="12,6"`); their combined width exceeds
  ~216px, so "Find it for me" runs past the card edge (clipped by the panel's `ClipToBounds`).
- The `TextBox` placeholder (`QueuePanel.axaml:169-172`) is a long string —
  "e.g. dotnet test · pytest · npm test · cargo test · go test ./..." — and placeholders do
  not ellipsize, so it clips to "e.g. dotnet test · pyte".
- The init empty state already has headless coverage in
  `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs`.

## What to build

### 1. Make `Border.chip` a real tag (global fix — `VisualRelayTheme.axaml:90-96`)
- Add `VerticalAlignment="Center"` to the style so a chip hugs its content height and centers
  within the row instead of stretching to button height; if any text still renders high, add a
  `Selector="Border.chip > TextBlock"` setter with `VerticalAlignment="Center"`. This corrects
  the off-center text everywhere at once.
- Restyle it to read as a non-interactive tag, clearly distinct from the bordered 36px buttons:
  pill `CornerRadius` (≈11+ at this height), a softer or no border, a slightly muted fill, and
  compact padding. Leave each instance's accent foreground (green/blue/amber) intact.

### 2. Fix the Initialize card (`QueuePanel.axaml:151-185`)
- Replace the overflowing horizontal `StackPanel` (`:173-183`) with a layout that fits the
  280px column — e.g. a `Grid ColumnDefinitions="*,*" ColumnSpacing="8"` with both buttons
  `HorizontalAlignment="Stretch"` (or a `WrapPanel`) — so neither button leaves the card.
- Shorten the placeholder (`:169-172`) to something that fits (e.g. "e.g. dotnet test") and
  move the full example list into the helper text above (`:165-168`) or a `ToolTip.Tip`, so
  nothing is clipped mid-word.

## Done when
- The count/status tags ("No run history", "0 pending", queue "0", stage/trace counts,
  run-log scope, pause notice) are vertically centered and visually distinct from buttons —
  they read as tags, not clickable controls. Verify with `./visual-relay screenshot`.
- The "Initialize this project" card keeps both action buttons inside its border at the 280px
  column width, and the test-command placeholder is fully readable.
- Purely presentational — commands and bindings are unchanged; compiled bindings stay clean.
- Existing headless UI tests still pass (`ConfigInitEmptyStateUiTests`); add an assertion if
  practical (e.g. the placeholder value / that the buttons share the row), but the primary
  check is the screenshot.
- `./visual-relay check` green; files < 300 lines; Conventional Commit subject (e.g.
  `fix(ui): restyle status tags and fix the init-card overflow`).
