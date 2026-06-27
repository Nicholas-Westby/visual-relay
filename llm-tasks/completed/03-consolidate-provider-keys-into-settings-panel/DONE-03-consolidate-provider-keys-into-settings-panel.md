# Clean up the Provider Keys panel and fold it into the Settings cog

The Provider Keys flyout is cramped and clips on the right: "Get a key" links and "Save"
buttons are cut off, a horizontal scrollbar appears, and the fixed 480px flyout — anchored to
the right-side "Keys" button — runs off the window's right edge. Clean up its layout, and
consolidate it into the single **Settings** panel (cog) so there is one tidy settings surface
instead of two separate top-bar buttons.

> **Depends on task `01-settings-cog-opt-out-of-committing-relay-proof`**, which introduces
> the `SettingsPanel` UserControl + the cog top-bar button. This is task **03** — build task
> 01 first. If `SettingsPanel` does not exist yet, create it here per task 01's UI section.

## Current state (researched)
- `KeySetupPanel` (`src/VisualRelay.App/Views/Controls/KeySetupPanel.axaml`, ~203 lines) is
  shown via a `Button.Flyout` with fixed `Width="480" MaxHeight="640"`, anchored to the
  right-aligned "Keys" TopBar button with `Placement="Bottom"` and toggled by
  `ToggleKeySetupCommand` (`src/VisualRelay.App/Views/Controls/TopBar.axaml:91-105`, grid
  `:11`). Because the button sits near the right edge and the flyout is bottom-placed and
  480px wide, the panel overflows the window's right edge and its content clips (a horizontal
  scrollbar appears).
- Each provider row is a `Grid ColumnDefinitions="Auto,*,Auto,Auto,Auto"` where the 5th column
  is unused; it holds the status dot, name (`*`), "(not set)" value, a "Get a key" link, and a
  second row with the token `TextBox` (`*`) + `Save`. There are **five near-identical ~30-line
  row blocks** (`KeySetupPanel.axaml:21-181`) for HF / DeepSeek / Moonshot / Anthropic / OpenAI.
- Test coupling: `tests/VisualRelay.Tests/KeySetupPanelUiTests.cs` asserts the panel renders
  and `vm.KeyStates.Count == 5` (VM-level, refactor-safe), but it also locates two **named**
  controls via `panel.FindControl<TextBox>("HfTokenInput")` (`:172`) and
  `FindControl<Button>("HfSaveButton")` (`:180`). Named controls inside an `ItemsControl`
  `DataTemplate` are not reachable by `FindControl`, so any de-duplication must keep those two
  addressable or update those two assertions.

## What to build

### 1. Fix the layout so nothing clips (primary)
- Constrain content to the flyout: wrap the panel body in a `ScrollViewer` with
  `HorizontalScrollBarVisibility="Disabled"` and vertical `Auto`; drop the dead 5th grid column
  and let the name column (`*`) absorb slack so rows fit the panel width.
- Keep the flyout on-screen: use a right-aligned placement (e.g.
  `Placement="BottomEdgeAlignedRight"`) and/or a width that fits, so opening it from the
  right-side cog never runs off the window edge.

### 2. Consolidate into Settings (depends on task `01`)
- Move Provider Keys into `SettingsPanel` as a clearly-titled section (e.g. "Provider keys"
  alongside the "Commit run proof" toggle from task 01), and **remove the separate "Keys"
  TopBar button + its flyout** (`TopBar.axaml:91-105`) so a single cog opens everything. Reuse
  the existing key VM (`KeyStates`, `SaveKeyCommand`, `OpenGetKeyUrlCommand`,
  `RefreshKeyStatesAsync`); `ToggleKeySetupCommand` can become the Settings open/refresh hook
  or be folded into the Settings toggle command.
- Size/scroll the combined Settings flyout so keys + toggles scroll vertically and never clip
  horizontally.

### 3. (Optional) De-duplicate the five rows
- Replace the five copy-pasted blocks with a single `ItemsControl` over `KeyStates` + one
  `DataTemplate` (removes ~150 lines, keeps rows consistent). If done, preserve test access to
  the HF row's input/save (e.g. traverse the visual tree to the first row, or keep the HF row
  un-templated) or update `KeySetupPanelUiTests.cs:172,180`. Keep only if it stays within the
  300-line limit and tests are updated in the same change.

## Done when
- Opening Settings (cog) shows Provider Keys and the run-proof toggle in one panel; the
  separate "Keys" top-bar button is gone.
- Nothing clips: every "Get a key" link, key field, and Save button is fully visible, there is
  no horizontal scrollbar, and the flyout stays within the window when opened from the
  right-aligned cog — verify with `./visual-relay screenshot` in the open state.
- Saving and reading each provider key still works: `KeySetupPanelUiTests` passes (updated only
  if the optional de-dup touched the named HF controls).
- `./visual-relay check` green; changed files < 300 lines; compiled bindings clean (no
  `$parent.DataContext` hops, no `x:CompileBindings="False"`); Conventional Commit subject
  (e.g. `refactor(ui): fold Provider Keys into the Settings panel`).
