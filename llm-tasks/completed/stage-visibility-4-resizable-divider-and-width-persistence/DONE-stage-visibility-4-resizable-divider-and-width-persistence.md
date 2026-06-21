# Make the center/right divider draggable and persist the panel width to XDG

Final part of per-stage prompt/output visibility (full design: `docs/superpowers/specs/2026-06-20-per-stage-prompt-output-tabs-design.md`). The Activity panel (a 5-tab control after task 3) can hold a ~70 KB input prompt, so let people widen it: add a draggable `GridSplitter` between the center column and the right Activity column, and persist the chosen width (and selected tab) to XDG user config so it survives restarts. The repo is shared host↔VM, so per-machine UI state MUST live in XDG, never in-repo.

**Depends on `stage-visibility-3`** (the tabbed `ActivityColumn` and its `ActivityTabIndex`). Complete it first if unmerged.

This design is decided — implement exactly this, no alternatives.

## Current state (researched)

- `MainWindow.axaml:39-42`: the content grid is `ColumnDefinitions="Auto,*,Auto"`, `ColumnSpacing="10"`, `Margin="10,16,10,10"` (queue | center | right). The right column (col 2) is a `Panel` (`:80-119`) holding `<controls:ActivityColumn Width="340" IsVisible="{Binding !IsActivityColumnCollapsed}"/>` plus a 36px rail `Border` shown when collapsed. The `340` is a literal; collapse is a content-swap (panel vs rail) inside the `Auto` track. The left QueuePanel uses the identical Auto-track swap (`:44-69`).
- Collapse state and toggles live in `MainWindowViewModel.Layout.cs` (`IsActivityColumnCollapsed` + its command).
- Settings/XDG precedent: `src/VisualRelay.Core/Configuration/KeyEnvFile.cs:35-56` resolves the config dir as `XDG_CONFIG_HOME` → `HOME/.config`, then `visual-relay/`, and stores **secrets** as dotenv lines. UI layout state must NOT go in that secrets file.
- MVVM = CommunityToolkit.Mvvm; the `MainWindowViewModel` is constructed once for the window.

## What to build

TDD — failing tests first.

1. **`UiStateStore`** (`src/VisualRelay.Core/Configuration/UiStateStore.cs`): reads/writes `<configDir>/visual-relay/ui-state.json` via `System.Text.Json`, reusing the same XDG resolution as `KeyEnvFile` (`XDG_CONFIG_HOME` → `HOME/.config`; factor the resolver if it is currently private to `KeyEnvFile`). API: `UiState Load()` (returns defaults when absent or corrupt — never throws) and `void Save(UiState)` (best-effort: creates the dir, swallows IO errors). `UiState` is a record `(double ActivityColumnWidth = 340, int ActivityTabIndex = 0)`. Unit-test: missing file → defaults; `Save`→`Load` round-trip under a custom `XDG_CONFIG_HOME` (use an env/temp seam so the test never touches the real `~/.config`); corrupt JSON → defaults.
2. **Resizable right column + splitter** in `MainWindow.axaml`: give the right Activity column a width driven two-way by `[ObservableProperty] private double _activityColumnWidth;` (default from the persisted value), and insert a `<GridSplitter>` between the center and right columns that resizes it. Clamp to a sensible range (min ≈ 300, max bounded by the window). Collapse-to-rail must still work: when `IsActivityColumnCollapsed`, show the 36px rail and hide/disable the splitter; when expanded, restore `ActivityColumnWidth`. Implement via a dedicated resizable column for the right panel with the `GridSplitter` to its left, keeping the existing panel/rail content-swap.
3. **Wire persistence.** Construct a `UiStateStore` in the VM; on startup apply `Load()` to `ActivityColumnWidth` and `ActivityTabIndex`. Persist on change via `partial void OnActivityColumnWidthChanged` and `partial void OnActivityTabIndexChanged`, each calling `Save(new UiState(ActivityColumnWidth, ActivityTabIndex))` (best-effort; no debounce required).

## Done when

- `UiStateStore` unit tests pass and fail against today's code first (defaults; round-trip under a custom `XDG_CONFIG_HOME`; corrupt-file tolerance).
- The center/right divider drags to resize the Activity panel; the width and the selected tab survive an app restart; collapse-to-rail still works; nothing is written into the repo (only `<configDir>/visual-relay/ui-state.json`).
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit, e.g. `feat(app): draggable activity divider with XDG-persisted width`.
- **Coordinate (implementer sees only this file):** this is the last task in the `stage-visibility-*` series; it completes the feature. It persists the `ActivityTabIndex` introduced in `stage-visibility-3`.
