# Add collapsible panels with a one-click "focus the task" toggle on the central TASK panel

Visual Relay's main window is a fixed three-column layout — Queue (left), the TASK
detail panel stacked over the Stage board (center), and the Activity column with
Run Log + LLM Commands (right) (`src/VisualRelay.App/Views/MainWindow.axaml:39-53`).
When reading a long task's markdown the center panel is boxed in by the side panels
and there is no way to give it more room.

Add two complementary affordances:

1. **Per-panel collapse** — every surrounding panel (Queue, Stages, Run Log, LLM
   Commands) gets its own collapse/expand toggle. A collapsed panel shrinks to a thin
   rail (horizontal collapse) or header strip (vertical collapse) that still shows its
   title and a chevron to bring it back.
2. **Master "focus the task" toggle on the TASK panel** — a single button in the TASK
   header collapses *all* the surrounding panels at once so the task detail fills the
   content area; once focused, the same control becomes a restore button that puts every
   panel back exactly as it was.

The intent is to let the operator maximize the task markdown while reading, then restore
the working layout in one click.

> **Implementation order:** task **04** of a batch, independent of the others except that it
> owns the new VM partial `MainWindowViewModel.Layout.cs` — `02-per-task-10x-turn-budget-toggle`
> is directed to a different partial (`MainWindowViewModel.TurnBudget.cs`) to avoid a collision.

## Current state (researched)

### Layout
- The content area is `Grid Grid.Row="2"` with `ColumnDefinitions="280,*,340"`,
  `ColumnSpacing="10"`, `Margin="10,16,10,10"` (`MainWindow.axaml:39-42`):
  - Col 0: `<controls:QueuePanel/>` — fixed 280 (`:43`).
  - Col 1: a nested `Grid RowDefinitions="1.45*,*" RowSpacing="10"` holding
    `<controls:TaskDetailPanel/>` (row 0) over `<controls:StageBoard Grid.Row="1"/>`
    (`:45-50`).
  - Col 2: `<controls:ActivityColumn x:Name="ActivityColumn"/>` — fixed 340 (`:52`).
- ActivityColumn is itself a `Grid RowDefinitions="*,*" RowSpacing="10"` of two
  `Border Classes="panel"`: RUN LOG (row 0) and LLM COMMANDS (row 1)
  (`src/VisualRelay.App/Views/Controls/ActivityColumn.axaml:7-8,68`).
- Every panel is a `Border Classes="panel"` whose header is a `Border`
  (`BorderThickness="0,0,0,1"`) wrapping a `Grid ColumnDefinitions="*,Auto"` — a
  `TextBlock Classes="panelTitle"` on the left, an actions cluster on the right:
  - TASK header: `TaskDetailPanel.axaml:15-46` (title `TASK`; right cluster = metric
    chip, status chip, `Run selected`, `Resume`).
  - QUEUE header: `QueuePanel.axaml:13-35` (title `{Binding TaskListTitle}`, count chip,
    `New`, Archive toggle; with Up/Down and the Bypass-sandbox checkbox below).
  - STAGES header: `StageBoard.axaml:9-25` (title `STAGES`, count chip).
  - RUN LOG header: `ActivityColumn.axaml:12-37`; LLM COMMANDS header: `:72-87`.

### Sizing mechanism / Avalonia constraint
- Side columns are fixed pixels (280 / 340); the center split is proportional
  (`1.45*,*`); the right column split is `*,*`.
- **Avalonia `ColumnDefinition`/`RowDefinition` are not in the control's logical/visual
  tree, so their `Width`/`Height` cannot be set with a normal `{Binding}` (no DataContext
  flows to them).** This rules out binding GridLengths directly in XAML. Two sanctioned
  mechanisms:
  - **Content-swap in an `Auto` track** (pure XAML): set the track to `Auto` and place
    both the full panel and a thin rail in it, toggling `IsVisible`; the `Auto` track
    follows whichever is shown. Ideal for the fixed 280/340 side columns — an `Auto`
    column tracking the 280-wide panel renders identically to today.
  - **Code-behind GridLength toggle**: the controls already have `.axaml.cs` files with
    the VM as DataContext; react to the collapse flag and set
    `ColumnDefinition.Width`/`RowDefinition.Height` between the current GridLength and a
    rail size. Best for the proportional center `1.45*,*` and right `*,*` splits, since it
    preserves the exact proportions when expanded.
- Avalonia 12.0.4 with compiled bindings on and warnings-as-errors
  (`VisualRelay.App.csproj:8,18`, `Directory.Build.props`); CommunityToolkit.Mvvm 8.4.1
  (`:26`).

### State conventions
- ViewModel state is `[ObservableProperty]`; commands are `[RelayCommand]`
  (`MainWindowViewModel.Commands.cs`). Computed props that depend on observable state use
  `[NotifyPropertyChangedFor(...)]` on the source (`MainWindowViewModel.Properties.cs`).
  `Commands.cs` is already 223 lines — put the new layout state/commands in a **new
  partial** `MainWindowViewModel.Layout.cs` to keep files under 300.
- Per-repo *behavior* settings persist to `.relay/config.json`
  (`MainWindowViewModel.Settings.cs`, `BypassSandbox`). Panel collapse is a transient
  per-machine *view* preference — do **not** persist it there.
- Shared styles live in `Styles/VisualRelayTheme.axaml` (`Border.panel`,
  `TextBlock.panelTitle`, `Border.chip`, `Button.primary`, …). Add any rail/toggle styles
  there.

## What to build

TDD — write the failing VM test first.

### 1. View-model: collapse state + commands (`MainWindowViewModel.Layout.cs`, new file)
- Four `[ObservableProperty] bool`: `IsQueueCollapsed`, `IsStagesCollapsed`,
  `IsRunLogCollapsed`, `IsLlmCommandsCollapsed` (default `false` = expanded).
- A per-panel toggle command each (`ToggleQueueCommand`, `ToggleStagesCommand`,
  `ToggleRunLogCommand`, `ToggleLlmCommandsCommand`) so buttons and tests share one entry
  point.
- Master focus:
  - `IsFocused` computed: `true` when all four flags are `true`. Decorate each flag with
    `[NotifyPropertyChangedFor(nameof(IsFocused))]` (and any master label/icon props).
  - `ToggleFocusCommand`: when **not** focused, snapshot the current four flags into a
    private field, then set all four `true` (collapse everything). When focused, restore
    the four flags from the snapshot (fallback: all `false`). This returns "restore" to
    *exactly* the pre-focus arrangement — satisfying "restore to the initial view" in the
    common case where nothing was individually collapsed.
  - Master button label/tooltip/icon derived from `IsFocused` (`Focus task` / ⤢ when not
    focused, `Restore panels` / ⤡ when focused).

### 2. Right-column width coupling
- The outer right column (340) narrows to a rail only when **both** RUN LOG and LLM
  COMMANDS are collapsed; collapsing just one keeps the 340 column and gives the freed
  space to the other (their split is `*,*`). Expose a computed
  `IsActivityColumnCollapsed => IsRunLogCollapsed && IsLlmCommandsCollapsed` for the
  column-width mechanism. (Master focus collapses both, so it reclaims the full right
  width.)

### 3. XAML / layout wiring
- Add a collapse chevron to each panel header's right-hand `Auto` cell: Queue
  (`QueuePanel.axaml`), Stages (`StageBoard.axaml`), Run Log + LLM Commands
  (`ActivityColumn.axaml`). Add the **master** toggle to the TASK header's right cluster
  (`TaskDetailPanel.axaml:19-45`), before `Run selected`.
- Collapsed representation:
  - **Queue** (left) and the **Activity column** (right) collapse **horizontally** to a
    ~36px vertical rail showing the title (rotated) + an expand chevron. The right rail
    offers both RUN LOG and LLM COMMANDS chevrons so each re-expands independently.
  - **Stages**, **Run Log**, **LLM Commands** collapse **vertically** to a header-only
    strip (keep the title + count chip + expand chevron; hide the body).
- Sizing: use **content-swap in `Auto` columns** for the left/right columns (preserves
  280/340 exactly), and **code-behind GridLength toggling** for the center `1.45*,*` and
  right `*,*` row splits (preserves proportions when expanded). The hard rule: **with
  nothing collapsed the window is pixel-identical to today.**
- Bind the new toggles against each control's existing `x:DataType="vm:MainWindowViewModel"`
  — no `$parent[...].DataContext` hops, no `x:CompileBindings="False"` (a concurrent task
  is removing those; don't reintroduce them).

## Done when
- Each surrounding panel (Queue, Stages, Run Log, LLM Commands) has its own visible
  collapse toggle; clicking it shrinks that panel to a rail/strip that still shows its
  title and an expand chevron, and the chevron restores it. The center TASK panel grows
  into the space freed by whichever panels are collapsed.
- The TASK header has a master toggle: when nothing (or only some) is collapsed it reads
  `Focus`/⤢ and collapses all four surrounding panels so the task detail fills the content
  area; once everything is collapsed it reads `Restore`/⤡ and returns every panel to its
  pre-focus state in one click.
- The right column narrows to a rail only when both Run Log and LLM Commands are
  collapsed; collapsing one alone reflows the column vertically without changing its width.
- With nothing collapsed, the layout is visually identical to the current build (same 280
  / center / 340 columns and 1.45:1 center split) — verify with `./visual-relay screenshot`.
- Collapse state is in-memory only (resets to the default all-expanded layout on
  relaunch); nothing is written to `.relay/config.json`.
- Tests first (plain VM tests + headless `[AvaloniaFact]` where a control is involved):
  toggling each flag flips only that panel; `ToggleFocus` from the default state collapses
  all four and sets `IsFocused`; `ToggleFocus` again restores the exact pre-focus flags;
  entering focus after individually collapsing one panel still restores that one collapsed
  on exit; `IsActivityColumnCollapsed` is true only when both right flags are set. No
  regression in existing panel/binding tests.
- `./visual-relay check` green; compiled bindings clean (no new reflection-hop or
  `x:CompileBindings="False"`); changed files under 300 lines; Conventional Commit
  subjects (e.g. `feat(ui): add collapsible panels and a task focus toggle`).
