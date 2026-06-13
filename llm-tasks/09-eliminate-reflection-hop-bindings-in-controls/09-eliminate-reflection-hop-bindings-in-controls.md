# Eliminate reflection-hop bindings so the compiled-bindings build gate covers every command binding

The app already opts every view into Avalonia compiled bindings
(`src/VisualRelay.App/VisualRelay.App.csproj:8`,
`AvaloniaUseCompiledBindingsByDefault=true`) and treats warnings as errors
(`Directory.Build.props:5`). With compiled bindings on, a binding to a member that
does not exist is a **build error** — so `./visual-relay check` already guards
binding correctness for free.

Two controls quietly escape that guard by binding through an *untyped* hop, which
either silently degrades to a runtime reflection binding or is explicitly opted
out of compilation. Both produce IDE errors ("Unable to resolve … in data context
of type 'object'") yet build clean, because the compiler never type-checks the
escaped segment. Rewrite both so their command bindings resolve against a typed
`x:DataType` — no `$parent[...].DataContext` walk, no `x:CompileBindings="False"`.
Once there are no reflection hops, the existing build gate catches any future typo
in these bindings.

> **Sequencing.** Middle task of a three-task group (08 → 09 → 10). Land **08
> (harden the test suite)** first — until then `./visual-relay check` stalls and
> can't go green, so this task's acceptance can't be verified. After this lands,
> **10 (adopt InspectCode standards repo-wide)** drives the project to zero findings:
> it treats these two binding escapes as real defects this task must fix (not
> carve-outs, since the StageBoard one is error-level), so correct the controls
> below before 10's compliance pass.

## Current state (researched)

### 1. StageBoard — silent reflection fallback through `object`

- The stage cards bind their click command by walking up to the parent VM:
  `Command="{Binding $parent[ItemsControl].DataContext.SelectStageCommand}"`
  with `CommandParameter="{Binding}"`
  (`src/VisualRelay.App/Views/Controls/StageBoard.axaml:40-41`). The `ItemTemplate`
  is typed `DataType="{x:Type vm:StageRowViewModel}"` (`StageBoard.axaml:37`).
- `Control.DataContext` is typed `object`, so the compiler cannot resolve
  `SelectStageCommand` on it and falls back to a reflection binding for that
  segment — no build error, but no type check either (and the IDE flags it).
- The command lives on the parent: `SelectStage(StageRowViewModel stage)` is a
  `[RelayCommand]` on the window VM
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs:185-207`),
  generated as `RelayCommand<StageRowViewModel> SelectStageCommand`. It toggles
  `_selectedStageFilter`, flips `IsSelected` across every row in `Stages`, sets
  `LogScopeLabel`, calls `ApplyLogFilter()`, and re-notifies
  `RevealStageArtifactsCommand`. The behavior is inherently parent-scoped and must
  stay on `MainWindowViewModel`.
- Rows are built in one place: `Stages.Add(new StageRowViewModel(stage))`
  (`MainWindowViewModel.cs:58-61`); `SelectStageCommand` is already available on
  `this` there. `StageRowViewModel` (`src/VisualRelay.App/ViewModels/StageRowViewModel.cs:7,22-28`)
  is a plain `ViewModelBase` taking only a `RelayStageDefinition`, with no command.

### 2. TaskDetailPanel attachment list — explicit `x:CompileBindings="False"`

- The Attachments tab lists `SelectedTask.SiblingPaths` in an `ItemsControl` that
  explicitly turns compilation **off**:
  `x:Name="AttachmentList" x:CompileBindings="False"`
  (`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:243-245`). The panel
  itself is `x:DataType="vm:MainWindowViewModel"` (`TaskDetailPanel.axaml:5`).
- Each item's DataContext is a bare **`string`** (a file path): the template has no
  `DataType`, the label is `Text="{Binding}"` (`TaskDetailPanel.axaml:247-258`), and
  the two action buttons walk up to the parent VM:
  `Command="{Binding $parent[UserControl].DataContext.RevealAttachmentCommand}"`
  and `…RemoveAttachmentCommand`, each with `CommandParameter="{Binding}"`
  (`TaskDetailPanel.axaml:260-272`). Compilation was disabled here precisely
  because a string item plus a `$parent.DataContext` hop will not compile.
- The commands take the path string:
  `RevealAttachment(string? filePath)` (`MainWindowViewModel.Authoring.cs:156`) and
  `RemoveAttachmentAsync(string? filePath)` (`MainWindowViewModel.Authoring.cs:122`,
  with confirmation at `:129-144`). `SiblingPaths` is `IReadOnlyList<string>`
  (`src/VisualRelay.Domain/RelayTaskItem.cs:8`, surfaced via
  `TaskRowViewModel.SiblingPaths`, `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs:42`).
- The "Add Attachment…" button binds `AddAttachmentsCommand` directly against the
  panel's `x:DataType` (`TaskDetailPanel.axaml:234`) and is already correct — leave
  it as is.

## What to build

Both fixes follow the same pattern: give the per-item view model the command it
needs, so the template binds against its own typed `x:DataType` with no
ancestor walk.

### StageBoard

- Add a constructor parameter to `StageRowViewModel` carrying the parent select
  command (e.g. `IRelayCommand<StageRowViewModel> selectCommand`) and expose it as
  a public `SelectCommand`. The row only surfaces the command it is handed; it
  does not reimplement selection logic.
- Wire it at `MainWindowViewModel.cs:58-61`:
  `new StageRowViewModel(stage, SelectStageCommand)`.
- In `StageBoard.axaml:40`, replace the hop with `Command="{Binding SelectCommand}"`,
  keeping `CommandParameter="{Binding}"` so `SelectStage` still receives the row.

### TaskDetailPanel attachment list

- Introduce a small typed item VM (e.g. `AttachmentRowViewModel`) exposing the
  display path plus a `RevealCommand` and `RemoveCommand` wired to the parent's
  `RevealAttachmentCommand`/`RemoveAttachmentCommand` (passing the path), mirroring
  the StageBoard pattern.
- Project the selected task's `SiblingPaths` into an
  `ObservableCollection<AttachmentRowViewModel>` on the window VM, rebuilt whenever
  `SelectedTask` changes (alongside the existing selection/load flow). The parent
  attachment commands stay the single source of truth.
- In `TaskDetailPanel.axaml`: point the `ItemsControl` at the new collection, set
  the `DataTemplate`'s `DataType` to `vm:AttachmentRowViewModel`, bind the label to
  the row's path property and each button to `RevealCommand`/`RemoveCommand`, and
  **remove `x:CompileBindings="False"`** (and the `$parent[UserControl].DataContext`
  hops) so the whole subtree compiles.

## Done when

- Neither `StageBoard.axaml` nor `TaskDetailPanel.axaml` contains a
  `$parent[...].DataContext` walk or an `x:CompileBindings="False"`; every command
  binding in both resolves against the template's own `x:DataType`. The IDE
  "Unable to resolve …" errors and the companion "possible 'null' value" notes on
  the affected lines are gone.
- **Behavior unchanged.** StageBoard: clicking a card still toggles its filter
  (click-again clears), updates `IsSelected` highlighting across all cards, sets
  `LogScopeLabel`, applies the log filter, and refreshes `RevealStageArtifactsCommand`
  can-execute. Attachments: the list still shows the selected task's siblings, and
  Reveal/Remove still invoke the parent commands with the correct path (Remove
  still confirms first). Selection/attachment logic is routed through the existing
  parent commands, not duplicated onto the item VMs.
- Tests (write the failing test first): constructing the stage list wires each
  row's `SelectCommand` and invoking it selects/toggles exactly as before;
  selecting a task projects its `SiblingPaths` into attachment rows whose
  Reveal/Remove commands carry the right path. No regressions in existing
  stage/log-filter or attachment tests.
- Verify with `./visual-relay screenshot` (stage cards highlight on click; the
  Attachments tab renders rows with working Reveal/Remove); confirm the IDE
  Problems pane is clean for both controls.
- `./visual-relay check` green; C#/XAML files stay under 300 lines; Conventional
  Commit subjects.
