# Convert the right column into a 5-tab Activity panel with per-stage prompt/output rendering

Part of per-stage prompt/output visibility (full design: `docs/superpowers/specs/2026-06-20-per-stage-prompt-output-tabs-design.md`). Replace the right column's two stacked, independently-collapsible panels (RUN LOG, LLM COMMANDS) with a single `TabControl`: **Run Log · Commands ⏐ System · Input · Output** (short labels; the three stage-scoped tabs set off by a divider). Bind the three new tabs to the `StageDetailViewModel` from task 2.

**Depends on `stage-visibility-2`** (`StageDetailViewModel`, `AssembledPromptParser`, `OutputFieldParser`). Complete it first if unmerged.

This design is decided — implement exactly this, no alternatives.

## Current state (researched)

- `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml` (165 lines) is `Grid RowDefinitions="*,*"` with two `Border` panels:
  - **RUN LOG** (`:11-77`): header with a `Reveal` button (`RevealStageArtifactsCommand`), a `LogScopeLabel` chip, and a collapse toggle (`ToggleRunLogCommand` / `RunLogChevron`); `ListBox ItemsSource="{Binding Events}"` `IsVisible="{Binding !IsRunLogCollapsed}"` with a `RelayEvent` `DataTemplate` using `DisplayLine`/`DetailLine`/`IsAttention`.
  - **LLM COMMANDS** (`:79-163`): header with a `{Binding TraceEntries.Count}` chip and a collapse toggle (`ToggleLlmCommandsCommand` / `LlmCommandsChevron`); `ListBox ItemsSource="{Binding TraceEntries}"` `IsVisible="{Binding !IsLlmCommandsCollapsed}"` with a `TraceEntry` `DataTemplate` using `Title`/`ScopeLabel`/`Content`.
- Hosted at `MainWindow.axaml:80-83` (`<controls:ActivityColumn Width="340" IsVisible="{Binding !IsActivityColumnCollapsed}"/>`); a 36px rail (`:84-118`) has two expand buttons (`ToggleRunLogCommand`, `ToggleLlmCommandsCommand`) and rotated "RUN LOG"/"LLM CMDS" labels.
- TabControl pattern to mirror: `TaskDetailPanel.axaml` (`<TabControl SelectedIndex="{Binding SelectedTabIndex}">` with `<TabItem Header="…">`), Avalonia default Fluent theme (no custom tab style).
- Views resolve by type-name via `ViewLocator`; child `UserControl`s inherit the `MainWindowViewModel` DataContext, so `{Binding Events}`, `{Binding TraceEntries}`, `{Binding StageDetail.*}` all resolve.

## What to build

TDD — add/adjust headless-UI tests first, following the existing headless-UI test pattern (see the `headless-ui-tests-for-config-init-empty-state` task and its tests).

1. **Extract the two existing panels into child views** (to stay under the 300-line guard): `Views/Controls/RunLogView.axaml` (the `Events` ListBox + its `RelayEvent` template) and `Views/Controls/CommandsView.axaml` (the `TraceEntries` ListBox + its `TraceEntry` template). Move the markup verbatim; they bind to the inherited `MainWindowViewModel`. Drop the per-panel `IsVisible="{Binding !Is*Collapsed}"` bindings (tabs replace per-panel collapse).
2. **Three stage tab views** bound to `StageDetail`: `Views/Controls/StageSystemView.axaml` (a `ScrollViewer` + `SelectableTextBlock` over `StageDetail.SystemPromptText`); `Views/Controls/StageInputView.axaml` (an `ItemsControl` over `StageDetail.InputSections`, each rendered as a collapsible `Expander` — `IsExpanded` defaults to `!CollapsedByDefault`, so "Prior stages" starts collapsed — with a Copy button and a raw-text toggle); `Views/Controls/StageOutputView.axaml` (an `ItemsControl` over `StageDetail.OutputFields` rendering `Label` + `Value` by `Kind`, plus a raw-JSON toggle bound to `StageDetail.RawJson`). Each view shows the context `Header`, and when its `*State` is not `Ready` shows the empty/transitional message: `NoStage` → "Click a stage to see its system prompt, input prompt, and output."; `NotStarted` → "Input prompt for {Header} will appear once the stage starts."; `NotComplete` → "Output for {Header} will appear once the stage completes."; `DriverStage` → "This stage runs git directly — no LLM prompt or output."
3. **Rebuild `ActivityColumn.axaml`** as panel chrome + a `TabControl SelectedIndex="{Binding ActivityTabIndex}"` hosting the five views in order `Run Log, Commands, System, Input, Output`. Keep the `Reveal` button + `LogScopeLabel` chip in the column header (they describe Run Log/Commands). Put a thin visual divider between the `Commands` and `System` tabs (a styled separator in the tab strip, or a leading separator visual on the `System` tab header).
4. **Collapse model change.** Replace the two independent collapses with ONE column collapse + tab selection. Add `[ObservableProperty] private int _activityTabIndex;` (default 0) to the VM. Keep `IsActivityColumnCollapsed` driving the rail swap as a single boolean toggled by one command (`ToggleActivityColumnCommand`); retire `IsRunLogCollapsed`/`IsLlmCommandsCollapsed` and their chevrons. Update the `MainWindow.axaml` rail (`:84-118`) to a single expand button + one rotated "ACTIVITY" label, and rename the column header title from "RUN LOG" to "ACTIVITY".

## Done when

- Headless-UI tests pass (and fail first): the column renders five tabs; selecting a stage populates System/Input/Output; with no stage selected the three stage tabs show "Click a stage…"; a not-yet-run stage shows the right transitional messages; a driver stage (Commit) shows the driver message; switching tabs updates `ActivityTabIndex`.
- Run Log and Commands behave exactly as before — still stage-filtered via `ApplyLogFilter`, still show their counts/labels.
- `./visual-relay check` green; every changed/new `.axaml`/`.cs` < 300 lines (this is why the panels are extracted); Conventional Commit, e.g. `feat(app): right column becomes a 5-tab activity panel`.
- **Coordinate (implementer sees only this file):** task `stage-visibility-4` adds the draggable divider and width persistence to `MainWindow.axaml` and the `ActivityColumnWidth` it binds, and persists the `ActivityTabIndex` you add here — do **not** change column widths, add the splitter, or wire persistence in this task.
