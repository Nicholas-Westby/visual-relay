## Stage 1 - Ideate

{
  "summary": "Remove square ListBoxItem chrome behind rounded task cards by scoping a minimal ControlTheme (TaskCardItemTheme) that strips highlight pseudoclass painting, correcting outer-ring CornerRadius from 8→10 and selection rail from 8→7,0,0,7, and encoding every visual claim — chrome-free items under forced pseudo-states, radius invariants (outer = card + ring), day-header isolation — in machine-checkable render-tree assertions, plus a structural guard test, all gated by ./visual-relay check.",
  "options": [
    "Option A — Keyed ControlTheme + radius literal fixes (recommended, closest to spec)",
    "Option B — Style-only approach (no ControlTheme, override via styles)",
    "Option C — Extract card content into a separate UserControl + wrap in Border at item level"
  ]
}

## Stage 2 - Research

{
  "findings": "Codebase investigation complete. The relevant files are: (1) QueuePanel.axaml (276 lines) — the ListBox DataTemplate nests an outer highlight Border (CornerRadius=8), inner queueCard Border (CornerRadius=8), selectionRail (CornerRadius=8). The ListBox has no ItemContainerTheme attribute. (2) VisualRelayTheme.axaml (188 lines) — sets ListBoxItem Background/Transparent at lines 60-68, but this only affects the control's own Background, not the Fluent theme's internal ContentPresenter pseudo-class fills. Also sets drop-above/drop-below BorderBrush/BorderThickness on the ListBoxItem (lines 71-78) and :pointerover recolor on Border.queueCard (lines 86-89). (3) TaskRowViewModel.cs — 4-state matrix (default/selected/running/running+selected) via computed brush properties; the outer ring is SelectedHighlightBorderBrush/Thickness/Shadow, inner card is CardBorderBrush/Thickness/Background/Shadow, rail is RailBrush. (4) ButtonControlThemes.axaml shows the Template setter pattern for ControlThemes (Style with ControlTemplate). (5) Tests: TaskRowViewModelTests.cs covers the 4-state brush matrix; ChevronAffordanceRenderTests.cs shows the headless render pattern (MainWindow + window.Show() + Dispatcher.UIThread.RunJobs() + visual tree walking via GetVisualDescendants); ButtonsCentralizationTests.cs shows structural guard pattern (regex source text scanning). (6) The DragDrop code-behind in QueuePanel.axaml.cs sets drop-above/drop-below CSS classes on the ListBoxItem itself, relying on those style selectors to render insertion lines via the item's BorderBrush/BorderThickness — the new ControlTheme must pass these through TemplateBinding. (7) No existing ItemContainerTheme or ControlTheme for ListBoxItem exists in the codebase. (8) Screenshot tooling at tools/VisualRelay.Screenshots/Program.cs seeds 5 demo tasks but only marks one running+selected; needs a plain pending card, a selected-not-running card, and a visible DayHeader added (step 5).",
  "constraints": [
    "QueuePanel.axaml must stay under 300 lines; currently at 276, so only the ItemContainerTheme attribute may be added to the <ListBox> declaration",
    "No global ListBoxItem behavior change — the scoped ControlTheme must be keyed and referenced only from this list via ItemContainerTheme",
    "Drag-reorder insertion lines (drop-above/drop-below) set BorderBrush/BorderThickness on the ListBoxItem itself; the new template must bind these via TemplateBinding to continue working",
    "The layered ring model must not be redesigned — selected/hover feedback must remain visible via the card's bound properties, not removed",
    "All files must pass ./visual-relay check (structural guards, format, build, tests, screenshot render)",
    "Conventional Commits format per docs/commit-messages.md",
    "The ControlTheme template must contain NO :pointerover/:pressed/:selected pseudo-class setters — state visuals belong exclusively to the card layer",
    "Headless render tests require [Collection(\"Headless\")] on the class and [AvaloniaFact] on each test method (enforced by SplitGuardVerificationTests)",
    "The ControlTheme must be placed in VisualRelayTheme.axaml's resources (which is 188 lines, well under any limit)",
    "Do not overwrite README screenshot assets unless the existing flow regenerates them; the verification screenshots are throwaway artifacts"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Five defects confirmed by reading the actual source files. D1/D2/D5: Fluent's ListBoxItem control theme (loaded via App.axaml line 12 <FluentTheme/>) paints square :pointerover/:pressed/:selected background fills on an internal ContentPresenter that no app-level style on ListBoxItem.Background or Border.queueCard can reach — verified by QueuePanel.axaml having no ItemContainerTheme (line 43) and VisualRelayTheme.axaml lines 60-68 only setting the control's own Background=Transparent. D3: QueuePanel.axaml line 56 outer Border CornerRadius='8' + SelectedHighlightBorderThickness=2, inner line 63 CornerRadius='8' — uniform ring needs outer=inner+thickness=10. D4: QueuePanel.axaml line 67 selectionRail CornerRadius='8' on all corners in 4px-wide column (line 64 ColumnDefinitions='4,*') — radius exceeds element width on right side. D5: DayHeader TextBlock (line 53) in Grid.Row='0' of same DataTemplate Grid; Fluent selection chrome spans the whole ContentPresenter including that row. Drag-reorder in QueuePanel.axaml.cs lines 167-169 sets drop-above/drop-below classes on ListBoxItem; styles at VisualRelayTheme.axaml lines 71-78 rely on BorderBrush/BorderThickness property propagation — new ControlTheme must TemplateBind these. No existing ControlTheme in the codebase (grep found zero).",
  "excerpts": [
    "QueuePanel.axaml:43-49 — ListBox x:Name='TaskQueueList' has no ItemContainerTheme attribute; gets default Fluent ListBoxItem chrome",
    "QueuePanel.axaml:56-67 — outer Border CornerRadius='8' (line 56), inner CornerRadius='8' (line 63), selectionRail CornerRadius='8' (line 67) in 4px column (line 64)",
    "VisualRelayTheme.axaml:60-68 — ListBoxItem Background=Transparent sets control property only; Fluent template paints its own ContentPresenter fills",
    "VisualRelayTheme.axaml:71-78 — drop-above/drop-below style selectors on ListBoxItem set BorderBrush/BorderThickness; new template must TemplateBind these",
    "VisualRelayTheme.axaml:86-89 — ListBoxItem:pointerover Border.queueCard recolor is correct per-card styling but can't remove Fluent's internal square :pointerover fill",
    "QueuePanel.axaml.cs:167-169 — drop-above/drop-below classes toggled directly on ListBoxItem control, relying on style property propagation",
    "App.axaml:12 — <FluentTheme/> loaded before app styles; its ListBoxItem template is the source of all square pseudo-class painting",
    "TaskRowViewModel.cs:94-98 — SelectedHighlightBorderBrush/BorderThickness/Shadow layered model is correct; SelectedHighlightBorderThickness=2 when selected",
    "ChevronAffordanceRenderTests.cs:16-24,203-215 — headless render test pattern: [Collection('Headless')], [AvaloniaFact], MainWindow+Show()+RunJobs(), visual tree walk via GetVisualDescendants()",
    "ButtonsCentralizationTests.cs:74-98 — structural guard pattern: regex source-text scanning for forbidden patterns"
  ],
  "repro": "1. Build and launch the app with demo tasks. 2. Select a task in the queue: observe a square blue selection band spanning the full ListBoxItem including the day header row, behind the correctly rounded blue ring of the card. 3. Hover over a card: observe a square full-item tint behind the rounded card. 4. Observe the selected card's blue outer ring appears pinched/squarish at corners (outer radius 8, inner 8, ring thickness 2 — should be outer=10). 5. Observe the 4px-wide selection rail stripe has CornerRadius='8' on all corners. 6. In archive view, selecting an item highlights the entire row including the 'Today ($1.04)' header."
}

## Stage 4 - Plan

{
  "plan": "## Concrete implementation plan for \"round-task-cards-in-every-state\"\n\n### 1. Neutralize ListBoxItem chrome — add keyed ControlTheme (fixes D1, D2, D5)\n\n**File: `src/VisualRelay.App/Styles/VisualRelayTheme.axaml`** (line ~68, after existing ListBoxItem:selected style)\n\nInsert a keyed `ControlTheme` named `TaskCardItemTheme` targeting `ListBoxItem`. It overrides Fluent's internal ContentPresenter fills that paint square pointerover/pressed/selected backgrounds. The template renders only:\n\n```xml\n<ControlTheme x:Key=\"TaskCardItemTheme\" TargetType=\"ListBoxItem\">\n  <Setter Property=\"Template\">\n    <ControlTemplate>\n      <Border Background=\"{TemplateBinding Background}\"\n              BorderBrush=\"{TemplateBinding BorderBrush}\"\n              BorderThickness=\"{TemplateBinding BorderThickness}\">\n        <ContentPresenter Name=\"PART_ContentPresenter\"\n                          Content=\"{TemplateBinding Content}\"\n                          ContentTemplate=\"{TemplateBinding ContentTemplate}\"\n                          Padding=\"{TemplateBinding Padding}\"/>\n      </Border>\n    </ControlTemplate>\n  </Setter>\n</ControlTheme>\n```\n\n- No `:pointerover`/`:pressed`/`:selected` pseudo-class setters — state is card-layer only.\n- TemplateBinding on BorderBrush/BorderThickness preserves the existing `.drop-above`/`.drop-below` drag-reorder insertion lines that set those properties on `ListBoxItem` control from `QueuePanel.axaml.cs:167-169`.\n\n**File: `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`** (line 43)\n\nAdd `ItemContainerTheme=\"{StaticResource TaskCardItemTheme}\"` to the `<ListBox>` opening tag:\n\n```xaml\n<ListBox Grid.Row=\"1\"\n         x:Name=\"TaskQueueList\"\n         ItemContainerTheme=\"{StaticResource TaskCardItemTheme}\"\n         Margin=\"10,0,10,8\"\n         ...>\n```\n\n### 2. Fix outer ring radius (D3)\n\n**File: `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`** (line 56)\n\nChange outer highlight Border `CornerRadius=\"8\"` → `CornerRadius=\"10\"` with comment:\n```xaml\n<Border Grid.Row=\"1\" CornerRadius=\"10\"  <!-- outer = inner 8 + ring thickness 2 -->\n```\n\n### 3. Fix selection rail radius (D4)\n\n**File: `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`** (line 67)\n\nChange `selectionRail` `CornerRadius=\"8\"` → `CornerRadius=\"7,0,0,7\"` (left corners only; 7 ≈ inner radius 8 minus the 1px card idle border):\n```xaml\n<Border Classes=\"selectionRail\"\n        Background=\"{Binding RailBrush}\"\n        CornerRadius=\"7,0,0,7\"/>\n```\n\n### 4. View-model matrix tests (4-state pinning)\n\n**File: `tests/VisualRelay.Tests/TaskRowViewModelTests.cs`**\n\nExisting tests cover default/selected/running/combined states individually. Add explicit matrix-table tests that assert all six visual properties (`SelectedHighlightBorderBrush`, `SelectedHighlightBorderThickness`, `SelectedHighlightShadow`, `CardBorderBrush`, `CardBorderThickness`, `CardShadow`) in all four states in a single parametrized sweep, plus `RailBrush` and `CardBackgroundBrush`. This pins the exact 4-state contract so no future change can drift a single property without the test catching it.\n\n### 5. Headless render/tree tests (chrome, radius, day-header isolation)\n\n**New file: `+tests/VisualRelay.Tests/TaskCardRenderTests.cs`**\n\nPattern follows `ChevronAffordanceRenderTests.cs`:\n- `[Collection(\"Headless\")]` on class, `[AvaloniaFact]` on each test method.\n- `CreateWindowWithTasks()` helper: builds `MainWindowViewModel`, adds tasks covering all four states (default, selected, running, running+selected) plus one with `DayHeader=\"Today ($1.04)\"`, shows window, runs dispatcher jobs, returns window.\n\nTests:\n1. **`ListBoxItems_HaveNoChromeBackground_InDefaultState`** — walk visual tree from each `ListBoxItem`'s root to the `Border.queueCard`; assert no element between paints a non-transparent `Background`.\n2. **`ListBoxItems_HaveNoChromeBackground_UnderForcedPseudoStates`** — force `:pointerover` and `:pressed` via `((IPseudoClasses)item.Classes).Set(\":pointerover\", true)`; assert no chrome background appears.\n3. **`ListBoxItems_HaveNoChromeBackground_WhenSelected`** — set `viewModel.SelectedTask` to genuinely select an item; assert no chrome background appears on the selected item.\n4. **`DayHeader_IsUntinted_WhenSelected`** — select the item with `DayHeader`; assert the header `TextBlock`'s ancestors up to the item root have transparent/no background (D5).\n5. **`CornerRadius_OuterRingEqualsCardPlusRingThickness`** — locate the outer highlight `Border` and inner `Border.queueCard`; assert `outer.CornerRadius == inner.CornerRadius + outer.BorderThickness.Left` (or equivalent). Pins the relationship, not just the literals.\n6. **`CornersRadius_LiteralsMatchSpec`** — assert outer ring `CornerRadius` literal is `10`, inner card is `8`, rail is `7,0,0,7`.\n\n### 6. Structural guard tests\n\n**New file: `+tests/VisualRelay.Tests/TaskCardThemeGuardTests.cs`**\n\nPattern follows `ButtonsCentralizationTests.cs`:\n1. **Assert `QueuePanel.axaml`'s `TaskQueueList` declares `ItemContainerTheme`** — scan the file text for the attribute on the `<ListBox>`.\n2. **Assert `TaskCardItemTheme` in `VisualRelayTheme.axaml` contains no `:pointerover`/`:pressed`/`:selected` pseudo-class setters** — regex scan of the theme file for forbidden patterns within the ControlTheme block.\n\n### 7. Demo screenshot refresh\n\n**File: `tools/VisualRelay.Screenshots/Program.cs`**\n\nExtend `BuildViewModel`:\n- Add a plain pending card (no selection, no running) — already present as the 2nd-5th tasks.\n- Add a selected-not-running card: set `viewModel.Tasks[1].IsSelected = true` after the initial selected (currently only `task` is selected). Actually, since only one task can be selected via `SelectedTask`, add a visible `DayHeader` to one of the existing demo tasks: `viewModel.Tasks[1].DayHeader = \"Yesterday\"`.\n- Ensure one card with `DayHeader` set renders in the scene.\n\n### 8. Full gate\n\nRun `./visual-relay check` to pass guards, format, build, tests, and screenshot render.\n\n---\n\n**Order of work**: Edit `VisualRelayTheme.axaml` → edit `QueuePanel.axaml` → extend `TaskRowViewModelTests.cs` → create `TaskCardRenderTests.cs` → create `TaskCardThemeGuardTests.cs` → extend `Program.cs` → run `./visual-relay check`.",
  "manifest": [
    "src/VisualRelay.App/Styles/VisualRelayTheme.axaml",
    "src/VisualRelay.App/Views/Controls/QueuePanel.axaml",
    "tests/VisualRelay.Tests/TaskRowViewModelTests.cs",
    "+tests/VisualRelay.Tests/TaskCardRenderTests.cs",
    "+tests/VisualRelay.Tests/TaskCardThemeGuardTests.cs",
    "tools/VisualRelay.Screenshots/Program.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/TaskRowViewModelTests.cs",
    "tests/VisualRelay.Tests/TaskCardRenderTests.cs",
    "tests/VisualRelay.Tests/TaskCardThemeGuardTests.cs"
  ],
  "rationale": "Three test files authored for the round-task-cards-in-every-state task. TaskRowViewModelTests.cs extended with a 32-case parametrized matrix sweep (Theory + MemberData) that pins all six visual brush/thickness/shadow properties across all four card states (default/selected/running/combined) in a single test — this passes now because the ViewModel logic is already correct. TaskCardRenderTests.cs is a new headless render-test class ([Collection(\"Headless\")], [AvaloniaFact]) that builds a real MainWindow with tasks covering all four states plus a DayHeader, then walks the visual tree to assert: (a) no element (Border or ContentPresenter) between each ListBoxItem root and its Border.queueCard paints a non-transparent Background in default/forced-:pointerover/:pressed/selected states (8 failures — D1/D2/D5 confirmed by ContentPresenter backgrounds #ff0078d7 and #19ffffff), (b) the outer ring CornerRadius equals the inner card CornerRadius plus the outer BorderThickness (fails: 8 vs expected 10 — D3), (c) literal CornerRadius values of outer=10, card=8, rail=7,0,0,7 (fails: outer is 8 — D3/D4). TaskCardThemeGuardTests.cs is a structural guard test that scans QueuePanel.axaml for ItemContainerTheme on the TaskQueueList ListBox (fails: attribute missing) and scans VisualRelayTheme.axaml for the TaskCardItemTheme ControlTheme containing no :pointerover/:pressed/:selected pseudo-class setters (fails: theme not found). All 8 target-behavior tests fail as expected; the 49 other tests (existing + the VM matrix sweep) pass. Targeted test command: dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter \"FullyQualifiedName~TaskRowViewModelTests|FullyQualifiedName~TaskCardRenderTests|FullyQualifiedName~TaskCardThemeGuardTests\""
}

## Stage 6 - Implement

{
  "summary": "Implemented round-task-cards-in-every-state across 4 files:\n\n1. **VisualRelayTheme.axaml** — Added keyed `TaskCardItemTheme` ControlTheme inside `Styles.Resources` that strips Fluent's square `:pointerover`/`:pressed`/`:selected` ContentPresenter fills. Template binds `Background`/`BorderBrush`/`BorderThickness` so drag-reorder insertion lines (`.drop-above`/`.drop-below`) still render. No pseudo-class setters — state visuals belong exclusively to the card's bound properties (D1, D2, D5).\n\n2. **QueuePanel.axaml** — Three changes: (a) `ItemContainerTheme=\"{StaticResource TaskCardItemTheme}\"` on the TaskQueueList ListBox to scope the chrome-free theme; (b) outer highlight Border `CornerRadius=\"8\"` → `\"10\"` with invariant comment (D3); (c) selectionRail `CornerRadius=\"8\"` → `\"7,0,0,7\"` for left-only curvature (D4).\n\n3. **TaskCardRenderTests.cs** — Compacted to 217 lines within the 300-line guard; all 6 assertions (chrome-free default/pseudo-states/selected, DayHeader isolation, radius invariant, literal radii) continue to encode D1–D5.\n\n4. **Program.cs (screenshots)** — Added `DayHeader = \"Today ($1.04)\"` to a demo task for day-header row coverage.\n\nFull gate: 2845 passed, 0 failed, 22 skipped (one pre-existing flaky timing test passed on re-run)."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": ["Demo screenshot refresh (tools/VisualRelay.Screenshots/Program.cs, BuildViewModel) is incomplete. Step 5 and the Done-when criterion require the rendered queue to show all four card states (pending, selected, running, running+selected) plus a day header, and step 5 explicitly says to 'add a ... selected-not-running card.' Only a DayHeader was added to Tasks[1]. The scene currently shows only state 1 (pending) and state 4 (running+selected, the `task` via RestoreRunningTaskState + SelectedTask) plus the day header; the standalone 'selected' (state 2, selected-not-running) and 'running' (state 3, running-not-selected) states are absent. Add a selected-not-running card (e.g. set a non-running task's IsSelected=true after SelectedTask is assigned, since the ring is VM-driven) and a running-not-selected card so all four states are visible for human review."] }

## Stage 8 - Fix

{
  "summary": "Fixed the incomplete demo screenshot seeding in tools/VisualRelay.Screenshots/Program.cs per review feedback. Added two lines after SelectedTask assignment: (1) viewModel.Tasks[2].MarkRunning() to produce a running-not-selected card (state 3), and (2) viewModel.Tasks[1].IsSelected = true to produce a selected-not-running card with the existing DayHeader (state 2). The existing task (index 0) already covers running+selected (state 4) and Tasks[3-4] cover pending (state 1). All four card states plus a day-header row are now rendered for human review. Targeted tests (57 passed, 0 failed) and full suite (2845 passed, 0 failed, 22 skipped) both pass."
}

## Stage 9 - Verify

{
  "summary": "Removed square ListBoxItem chrome behind rounded task cards by adding a keyed `TaskCardItemTheme` ControlTheme (in VisualRelayTheme.axaml) that strips Fluent's internal :pointerover/:pressed/:selected ContentPresenter fills via a minimal Border+ContentPresenter template, scoped via `ItemContainerTheme` on QueuePanel's TaskQueueList. Fixed outer ring CornerRadius from 8→10 (outer = inner 8 + ring thickness 2, D3) and selection rail from all-8→\"7,0,0,7\" (left corners only, D4). Extended Program.cs to seed all four card states plus a DayHeader for human review. Enforced all claims with: (1) a 32-case view-model matrix sweep in TaskRowViewModelTests.cs, (2) headless render-tree assertions in TaskCardRenderTests.cs (chrome-free under default/forced-:pointerover/:pressed/selected, day-header isolation, outer=card+ring radius invariant, literal CornerRadius checks), and (3) structural source-text guards in TaskCardThemeGuardTests.cs (ItemContainerTheme presence, forbidden pseudo-class setters). Every file stays under 300 lines; drag-reorder insertion lines (drop-above/drop-below) continue working via TemplateBinding of BorderBrush/BorderThickness.",
  "commitMessages": [
    "fix: strip square ListBoxItem chrome behind rounded task cards with keyed ControlTheme",
    "fix(queue): remove Fluent selection/hover/press backgrounds from task list items",
    "fix: correct outer ring radius to 10 and selection rail to 7,0,0,7 for rounded cards",
    "test: pin chrome-free rendering and radius invariants with headless tree assertions",
    "fix: scope chrome-free ListBoxItem template to task queue via ItemContainerTheme"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

