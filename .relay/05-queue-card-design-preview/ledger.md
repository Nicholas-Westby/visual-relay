## Stage 1 - Ideate

{
  "summary": "The Avalonia previewer renders garbage because QueuePanel.axaml has no design-time DataContext and the ViewModel has empty Tasks. The fix: (1) create DesignData.cs with pre-populated view models, (2) extract the card DataTemplate (~82 lines) into TaskCard.axaml, (3) extract the footer (~83 lines) into QueueFooter.axaml, (4) wire Design.* attributes on QueuePanel, TaskCard, QueueFooter, MainWindow, and VisualRelayTheme, (5) update ContrastTests to scan all three panel XAML files, and update StatusFooterFlyoutTests to resolve QueueFooter then FindControl on it. No runtime behavior changes, no new packages, all existing tests pass unmodified or with the prescribed changes.",
  "options": [
    "Execute the full plan as spec'd: create DesignData.cs, extract TaskCard.axaml+.cs, extract QueueFooter.axaml+.cs, wire Design.* attributes, update ContrastTests and StatusFooterFlyoutTests, add DesignDataTests.cs and QueuePanelSplitRenderTests.cs",
    "Only add DesignData.cs and wire Design.* attributes without extracting TaskCard/QueueFooter (but this leaves QueuePanel.axaml at 284+ lines with the risk of hitting the 300-line FileSizeGuard when adding design attributes)",
    "Only add DesignData.cs and point MainWindow.axaml Design.DataContext at it (partial fix — improves MainWindow preview but still leaves standalone QueuePanel.axaml preview broken and doesn't address line count)"
  ]
}

## Stage 2 - Research

{
  "findings": "QueuePanel.axaml (284 lines) has no design-time DataContext, causing the Avalonia previewer to render all mutually-exclusive panels stacked (init state, config-diagnostic, HF-gate footer, status strip) because bindings fail silently. MainWindow.axaml has a Design.DataContext with a bare MainWindowViewModel whose Tasks collection is empty — useless preview. The card DataTemplate (~82 lines) and two footer Borders (~83 lines) occupy most of QueuePanel.axaml's bulk, leaving ~125 lines after extraction. DesignData.cs must construct pre-populated TaskRowViewModel instances in-memory: RecordStageCompleted is internal (same-assembly access works), MarkRunning/IsSelected/RunningElapsedLabel are public. ShowHfGate depends on _keyStatesLoaded which stays false at design time, so the status strip (not HF gate) shows. OnSelectedTaskChanged fires SelectTaskAsync which performs disk I/O — SelectedTask must remain null in DesignData. ContrastTests.LoadQueuePanel() scans QueuePanel.axaml by path and must be extended to all three XAML files. StatusFooterFlyoutTests does queuePanel.FindControl<CommonButton>('StatusExpandButton') which will fail after extraction to QueueFooter — must resolve QueueFooter first via GetVisualDescendants. TaskCardRenderTests walks MainWindow → QueuePanel → ListBox → ListBoxItem → Border.queueCard — the extraction preserves this visual ancestry since TaskCard is a UserControl wrapping the same Grid. TaskCardThemeGuardTests scans for x:Name='TaskQueueList' on a line that also contains ItemContainerTheme — both stay on one line in QueuePanel.axaml. The thin code-behind idiom is CommandsView.axaml.cs (public partial class ... : UserControl with only InitializeComponent()). HeadlessCollectionFiles_HaveCollectionAttribute in SplitGuardVerificationTests.Conventions.cs has a hardcoded list of 7 files and must NOT be updated.",
  "constraints": [
    "Every touched or created .cs/.axaml file must stay ≤ 300 lines (FileSizeGuard hard limit)",
    "No new NuGet packages or external dependencies of any kind",
    "Moved XAML must be byte-identical except for stripped Grid.Row='2' attributes and new UserControl roots — no reordering, re-indentation, or color/spacing changes",
    "Use Avalonia's Design.* attached properties (Design.DataContext, Design.Width, Design.Height, Design.PreviewWith), NOT Blend d:/mc:Ignorable namespaces",
    "Do NOT set Main.SelectedTask in DesignData (triggers SelectTaskAsync disk I/O)",
    "Do NOT extract the InitEmptyState 'Initialize this project' card or config-diagnostic box",
    "Do NOT rename any x:Name'd control (TaskQueueList, StatusExpandButton, InitEmptyState, InitTestCommandBox, CreateConfigButton)",
    "Do NOT touch drag-reorder logic in QueuePanel.axaml.cs",
    "Do NOT split DayHeader out of TaskCard",
    "Do NOT add QueuePanelSplitRenderTests.cs or DesignDataTests.cs to HeadlessCollectionFiles_HaveCollectionAttribute fixed list",
    "TaskCard root must NOT set Background (preserves transparency chain for TaskCardRenderTests)",
    "ListBox line in QueuePanel.axaml must keep x:Name='TaskQueueList' and ItemContainerTheme on the same line",
    "New QueuePanelSplitRenderTests must use [Collection('Headless')] and host controls in bare Window with window.Show()+Dispatcher.UIThread.RunJobs() — NOT boot MainWindow",
    "DesignDataTests uses plain [Fact] (no Avalonia session) — pure view-model assertions only",
    "If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove coverage"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "QueuePanel.axaml (284 lines) declares x:DataType=\"vm:MainWindowViewModel\" at line 7 but has no Design.DataContext attribute — the Avalonia previewer renders the file with null DataContext, so every binding fails silently: ItemsSource yields zero cards, and all IsVisible bindings default to True, stacking the InitEmptyState card, config-diagnostic box, and both footer Borders simultaneously. MainWindow.axaml lines 20-22 provide a Design.DataContext, but it constructs a bare MainWindowViewModel whose Tasks collection is empty (MainWindowViewModel.cs line 69: `public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];`), so the whole-window preview shows an empty queue. Card visuals (AccentBrush, RailBrush, CardBackgroundBrush, SelectedHighlightBorderBrush, CardBorderBrush, CardShadow, ProgressFraction) are all computed properties in TaskRowViewModel.cs lines 99-110 from runtime state flags (IsRunning/IsSelected/NeedsReview) using Brush.Parse statics (lines 10-21) — no preview without populated TaskRowViewModel instances. The card DataTemplate (QueuePanel.axaml lines 50-135, ~85 lines) and footer Borders (lines 199-281, ~83 lines) occupy ~168 of the file's 284 lines, leaving only ~116 lines of headroom against the FileSizeGuard default limit of 300 (FileSizeGuard.cs line 13). No DesignData.cs, TaskCard.axaml, or QueueFooter.axaml exist yet. Setting Main.SelectedTask would fire OnSelectedTaskChanged (MainWindowViewModel.Commands.cs line 198) → SelectTaskAsync (line 203) → ReadTaskInputAsync (line 232), reading markdown from disk and polluting StatusText. ShowHfGate depends on _keyStatesLoaded (MainWindowViewModel.Keys.cs line 68, defaults to false) so the status strip (not HF gate) shows at design time. Four test suites must remain unmodified: TaskCardRenderTests walks ListBoxItem → visual descendants → Border.queueCard (TaskCardRenderTests.cs lines 175-177); TaskCardThemeGuardTests scans QueuePanel.axaml for the x:Name=\"TaskQueueList\" line also containing ItemContainerTheme (line 39); ConfigInitEmptyStateUiTests and InitPanelButtonsLayoutTests resolve named controls (InitEmptyState, InitTestCommandBox, CreateConfigButton) in QueuePanel's namescope. ContrastTests.LoadQueuePanel (line 235) and StatusFooterFlyoutTests queuePanel.FindControl<CommonButton>(\"StatusExpandButton\") (line 45) will break after extraction and require the prescribed updates.",
  "excerpts": [
    "QueuePanel.axaml:1-7: <UserControl xmlns=\"https://github.com/avaloniaui\" ... x:Class=\"VisualRelay.App.Views.Controls.QueuePanel\" x:DataType=\"vm:MainWindowViewModel\">  (no Design.DataContext)",
    "MainWindow.axaml:20-22: <Design.DataContext> <vm:MainWindowViewModel/> </Design.DataContext>",
    "MainWindowViewModel.cs:69: public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];",
    "TaskRowViewModel.cs:10-21: private static readonly IBrush SelectedBrush = Brush.Parse(\"#3191FF\"); ... private static readonly BoxShadows RunningShadow = BoxShadows.Parse(\"0 0 22 0 #445AD47D\");",
    "TaskRowViewModel.cs:99-110: public IBrush AccentBrush => IsRunning ? RunningBrush : NeedsReview ? ReviewBrush : SelectedBrush; ... public double ProgressFraction => IsRunning ? Math.Clamp(_liveCompletedStageCount / (double)RelayStages.All.Count, 0, 1) : Math.Clamp(Task.CompletedStageCount / (double)RelayStages.All.Count, 0, 1);",
    "FileSizeGuard.cs:13: private const int DefaultLimit = 300;",
    "MainWindowViewModel.Commands.cs:198: LastSelectionLoad = SelectTaskAsync(value); (called from OnSelectedTaskChanged)",
    "MainWindowViewModel.Commands.cs:232: var input = await new RelayTaskRepository(RootPath).ReadTaskInputAsync(task.Task); (disk I/O in SelectTaskAsync)",
    "MainWindowViewModel.Keys.cs:68: private bool _keyStatesLoaded; (defaults false)",
    "MainWindowViewModel.Keys.cs:86: public bool ShowHfGate => _keyStatesLoaded && !IsHuggingFaceConfigured;",
    "TaskCardRenderTests.cs:175-177: private static Border? FindQueueCard(ListBoxItem item) => item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(\"queueCard\"));",
    "TaskCardThemeGuardTests.cs:37-40: if (lines[i].Contains(\"x:Name=\\\"TaskQueueList\\\"\")) { Assert.True(lines[i].Contains(\"ItemContainerTheme\"), ...); }",
    "ContrastTests.cs:235-241: private static XElement LoadQueuePanel() { var path = Path.Combine(RepoSetup.Root, \"src\", \"VisualRelay.App\", \"Views\", \"Controls\", \"QueuePanel.axaml\"); ... return XDocument.Load(path).Root!; }",
    "StatusFooterFlyoutTests.cs:45: var expandButton = queuePanel.FindControl<CommonButton>(\"StatusExpandButton\");"
  ],
  "repro": "1. Open src/VisualRelay.App/Views/Controls/QueuePanel.axaml in Rider or the Avalonia previewer. Observe that no cards render, and the \"Initialize this project\" card, config-diagnostic box, HF-gate footer, and status strip all render simultaneously stacked — the previewer has no DataContext so every binding fails. 2. Open src/VisualRelay.App/Views/MainWindow.axaml in the previewer. Observe an empty queue panel with no task cards — the bare MainWindowViewModel's Tasks collection is empty. 3. Open src/VisualRelay.App/Styles/VisualRelayTheme.axaml in the previewer. Observe a blank preview — this styles-only file has no preview host. 4. Open src/VisualRelay.App/ViewModels/TaskRowViewModel.cs and note that every visual property (AccentBrush, CardBackgroundBrush, RailBrush, etc.) is computed from runtime state flags (IsRunning/IsSelected/NeedsReview) — there is no way to mock these states from XAML without real TaskRowViewModel instances. 5. Run `./visual-relay check` to confirm QueuePanel.axaml (284 lines) is under but close to the 300-line FileSizeGuard limit, and that adding Design.* attributes plus future styling work would exceed it."
}

## Stage 4 - Plan

{
  "plan": "## Concrete implementation plan\n\n### Step 1 — Create `src/VisualRelay.App/DesignTime/DesignData.cs`\n\nNew static class `DesignData` with two static properties:\n- `Card`: a `TaskRowViewModel` with 8 completed stages via `RecordStageCompleted(1..8)`, `MarkRunning(9, \"Fix\")`, `RunningElapsedLabel = \"14m 12s\"`, `IsSelected = true`. This is the richest single-card preview.\n- `Main`: a `MainWindowViewModel` with 5 tasks covering every card state: running+selected (`Card`), pending, needs-review (with `reviewReason`), no-run-history (`CompletedStageCount = 0`), archived (with `DayHeader = \"Today ($0.42)\"`). `StatusText` is set. `SelectedTask` is deliberately left `null` to avoid `SelectTaskAsync` disk I/O.\n\nA private `NewItem` helper constructs `RelayTaskItem` records with home-anchored paths (e.g. `~/Dev/acme/llm-tasks/03-survive-display-sleep-at-launch/03-survive-display-sleep-at-launch.md`) so `HomePathToTildeConverter` renders `~/Dev/acme/…` in previews. `RecordStageCompleted` is `internal` and compiles because `DesignData` lives in the same assembly (`VisualRelay.App`).\n\n### Step 2 — Extract card DataTemplate into `TaskCard.axaml` + `.axaml.cs`\n\n**`TaskCard.axaml`**: UserControl root with `xmlns:vm`, `xmlns:controls`, `xmlns:dt`, `x:Class=\"VisualRelay.App.Views.Controls.TaskCard\"`, `x:DataType=\"vm:TaskRowViewModel\"`, `Design.DataContext=\"{x:Static dt:DesignData.Card}\"`, `Design.Width=\"320\"`. Body is the exact XML from `QueuePanel.axaml` lines 52-133 — the `<Grid RowDefinitions=\"Auto,*\">` containing the `DayHeader` TextBlock and the two nested card `Border`s — preserved byte-for-byte (comments included). Root does **not** set `Background`.\n\n**`TaskCard.axaml.cs`**: `public partial class TaskCard : UserControl` with only `InitializeComponent()` (thin code-behind idiom from `CommandsView.axaml.cs`).\n\n**`QueuePanel.axaml`**: lines 50-135 (the entire `ListBox.ItemTemplate` + `DataTemplate` block) replaced with:\n```xml\n<ListBox.ItemTemplate>\n  <DataTemplate DataType=\"{x:Type vm:TaskRowViewModel}\">\n    <controls:TaskCard/>\n  </DataTemplate>\n</ListBox.ItemTemplate>\n```\nThe `ListBox` line (with `x:Name=\"TaskQueueList\"` and `ItemContainerTheme`) stays exactly as-is on a single line.\n\n### Step 3 — Extract footer into `QueueFooter.axaml` + `.axaml.cs`\n\n**`QueueFooter.axaml`**: UserControl root with `xmlns:vm`, `xmlns:buttons`, `xmlns:dt`, `x:Class=\"VisualRelay.App.Views.Controls.QueueFooter\"`, `x:DataType=\"vm:MainWindowViewModel\"`, `Design.DataContext=\"{x:Static dt:DesignData.Main}\"`, `Design.Width=\"340\"`. Body: a `<Panel>` wrapping the two footer `Border`s from `QueuePanel.axaml` lines 199-281 with `Grid.Row=\"2\"` stripped from both, preserved byte-for-byte otherwise.\n\n**`QueueFooter.axaml.cs`**: thin code-behind.\n\n**`QueuePanel.axaml`**: lines 199-281 replaced with `<controls:QueueFooter Grid.Row=\"2\"/>`.\n\n### Step 4 — Add Design.* attributes to QueuePanel.axaml root\n\nAdd to the existing `<UserControl>` element (keeping all existing attributes):\n```xml\nxmlns:dt=\"using:VisualRelay.App.DesignTime\"\nDesign.DataContext=\"{x:Static dt:DesignData.Main}\"\nDesign.Width=\"340\" Design.Height=\"780\"\n```\n\n### Step 5 — Wire MainWindow.axaml Design.DataContext\n\nAdd `xmlns:dt=\"using:VisualRelay.App.DesignTime\"` to the `<Window>` root. Replace the three-line `<Design.DataContext><vm:MainWindowViewModel/></Design.DataContext>` element (lines 20-22) with the attribute `Design.DataContext=\"{x:Static dt:DesignData.Main}\"` on the `<Window>` element. Keep the existing `d:` Blend attributes.\n\n### Step 6 — Add preview host to VisualRelayTheme.axaml\n\nAdd `<Design.PreviewWith>` as the first child of the `<Styles>` root:\n```xml\n<Design.PreviewWith>\n  <Border Width=\"360\" Height=\"800\" Padding=\"10\" Background=\"#0C0E12\">\n    <controls:QueuePanel/>\n  </Border>\n</Design.PreviewWith>\n```\n`xmlns:controls` already exists in the `<Styles>` root.\n\n### Step 7 — Update ContrastTests.cs\n\nReplace `LoadQueuePanel()` (returns single root) with:\n```csharp\nprivate static readonly string[] PanelXamlFiles =\n    [\"QueuePanel.axaml\", \"TaskCard.axaml\", \"QueueFooter.axaml\"];\n\nprivate static IEnumerable<XElement> LoadPanelRoots() { … } // yields all three roots\n\nprivate static IEnumerable<XElement> PanelTextElements() =>\n    LoadPanelRoots().SelectMany(TextElements);\n```\n\nUse `PanelTextElements()` at all three former `TextElements(LoadQueuePanel())` call sites. Rename the three `QueuePanel_…` fact/theory methods to `PanelXamls_…`. Update the class and section doc comments to name all three files.\n\n`ResolveTextSurface` is **not** changed. After extraction:\n- `DayHeader` TextBlock in `TaskCard.axaml` walks to the UserControl root (no `Background` attribute) → resolves to `PanelBackground`. ✓\n- `STATUS` label in `QueueFooter.axaml` walks to `Border Background=\"#1B2028\"` → resolves to `\"#1B2028\"`. ✓\n- Card-interior text still hits `Border.queueCard Background=\"{Binding CardBackgroundBrush}\"` (non-hex) → returns `null`, excluded from both scans. ✓\n\n### Step 8 — Update StatusFooterFlyoutTests.cs\n\nIn all four `[AvaloniaFact]`s, replace:\n```csharp\nvar expandButton = queuePanel.FindControl<CommonButton>(\"StatusExpandButton\");\n```\nwith:\n```csharp\nvar footer = window.GetVisualDescendants().OfType<QueueFooter>().Single();\nvar expandButton = footer.FindControl<CommonButton>(\"StatusExpandButton\");\n```\n\nAdd `using VisualRelay.App.Views.Controls;` (already present). Update the class doc comment: footer is hosted at QueuePanel's `Grid.Row=\"2\"` but defined in `QueueFooter.axaml`.\n\n### Step 9 — Create `tests/VisualRelay.Tests/DesignDataTests.cs`\n\nThree `[Fact]`s (no Avalonia session — pure view-model assertions):\n- `Main_CoversEveryCardState`: exactly one `IsRunning`, ≥1 `NeedsReview && !IsRunning`, ≥1 `IsArchived`, ≥1 `Task.CompletedStageCount == 0 && !IsRunning`, ≥1 non-empty `DayHeader`, exactly one `IsSelected`.\n- `Main_LeavesSelectedTaskNull_SoPreviewsDoNoDiskIo`: `Assert.Null(DesignData.Main.SelectedTask)` with a comment naming `SelectTaskAsync` disk-read reason.\n- `Card_IsRunningSelectedMidProgress`: `Card.IsRunning`, `Card.IsSelected`, `Assert.InRange(Card.ProgressFraction, 0.01, 0.99)`, `MetricsLine` contains `\"Stage 09 · Fix\"`.\n\n### Step 10 — Create `tests/VisualRelay.Tests/QueuePanelSplitRenderTests.cs`\n\n`[Collection(\"Headless\")]`, `public sealed class`, two `[AvaloniaFact]`s:\n- `TaskCard_RendersStandalone_FromDesignData`: hosts `new TaskCard { DataContext = DesignData.Card }` in a bare `Window` (`window.Show()` + `Dispatcher.UIThread.RunJobs()`), asserts a `Border.queueCard` descendant exists and the single `ProgressBar`'s `Value` equals `DesignData.Card.ProgressFraction`.\n- `QueueFooter_HostsExpandButton_InOwnNameScope`: hosts `new QueueFooter { DataContext = DesignData.Main }` in a bare `Window`, asserts `footer.FindControl<CommonButton>(\"StatusExpandButton\")` is non-null with a `Flyout`.\n\n### Step 11 — Bump ContrastTests ratchet in SplitGuardVerificationTests.cs\n\n`FactCount_AcrossOversizedFiles_MatchesBaseline` counts `[Fact]`s only across oversized-file families (prefix list). `DesignDataTests.cs` and `QueuePanelSplitRenderTests.cs` are new standalone files **not** in the prefix list, so the baseline of 175 is unchanged and needs **no bump**.\n\nThe fixed list in `HeadlessCollectionFiles_HaveCollectionAttribute` is **not** updated (per spec: legacy pin, not a registry).\n\n### Verification\n\n- `./visual-relay check` must pass: all touched `.cs`/`.axaml` files ≤ 300 lines.\n- Expected line counts: QueuePanel.axaml ~123, TaskCard.axaml ~91, QueueFooter.axaml ~91, DesignData.cs ~80, code-behinds ~12 each, tests ~50-80 each.\n- Existing suites pass unmodified: `TaskCardRenderTests`, `TaskCardThemeGuardTests`, `ConfigInitEmptyStateUiTests`, `InitPanelButtonsLayoutTests`.\n- `ContrastTests` and `StatusFooterFlyoutTests` pass with the prescribed updates only.",
  "manifest": [
    "+src/VisualRelay.App/DesignTime/DesignData.cs",
    "+src/VisualRelay.App/Views/Controls/TaskCard.axaml",
    "+src/VisualRelay.App/Views/Controls/TaskCard.axaml.cs",
    "+src/VisualRelay.App/Views/Controls/QueueFooter.axaml",
    "+src/VisualRelay.App/Views/Controls/QueueFooter.axaml.cs",
    "src/VisualRelay.App/Views/Controls/QueuePanel.axaml",
    "src/VisualRelay.App/Views/MainWindow.axaml",
    "src/VisualRelay.App/Styles/VisualRelayTheme.axaml",
    "tests/VisualRelay.Tests/ContrastTests.cs",
    "tests/VisualRelay.Tests/StatusFooterFlyoutTests.cs",
    "+tests/VisualRelay.Tests/DesignDataTests.cs",
    "+tests/VisualRelay.Tests/QueuePanelSplitRenderTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 5, untracked deleted: 5.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/DesignDataTests.cs",
    "tests/VisualRelay.Tests/QueuePanelSplitRenderTests.cs"
  ],
  "rationale": "Created DesignDataTests.cs (3 [Fact]s — pure view-model assertions covering card state coverage, SelectedTask null guard, and Card mid-progress properties) and QueuePanelSplitRenderTests.cs (2 [AvaloniaFact]s — standalone TaskCard/QueueFooter rendering from DesignData in bare Windows). Both verify the design-time data and extracted controls render correctly without the full MainWindow boot. All 3,023 tests pass, all files under 300-line limit."
}

## Stage 6 - Implement

{
  "summary": "All 12 manifest changes implemented. Created DesignData.cs with pre-populated Card (running, selected, mid-progress) and Main (5 tasks covering all card states). Extracted card DataTemplate into TaskCard.axaml (+ thin code-behind) and footer into QueueFooter.axaml (+ thin code-behind). Updated QueuePanel.axaml with Design.* attributes (Design.DataContext, Design.Width, Design.Height), TaskCard reference in DataTemplate, and QueueFooter reference. Updated MainWindow.axaml with Design.DataContext pointing to DesignData.Main. Added Design.PreviewWith host to VisualRelayTheme.axaml. Updated ContrastTests.cs: replaced LoadQueuePanel() with LoadPanelRoots() scanning all three XAML files, renamed methods from QueuePanel_* to PanelXamls_*, removed dead code. Updated StatusFooterFlyoutTests.cs: all 4 facts now resolve QueueFooter via GetVisualDescendants before FindControl. DesignDataTests.cs (3 Facts) and QueuePanelSplitRenderTests.cs (2 AvaloniaFacts) already existed from stage 5. All 3023 tests pass, 0 failures, 113 skipped."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: No runtime visuals change. All moved XAML is byte-identical; Design.* attributes are stripped by the XAML compiler outside the designer. Existing test suites (TaskCardRenderTests, ContrastTests, TaskCardThemeGuardTests) guard the visual invariants. Previewer-only improvements are intentional and documented. A vision pass would add no signal._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Implemented design-time preview data and componentized QueuePanel under the 300-line FileSizeGuard. Created `DesignData.cs` with pre-populated `Card` (running, selected, mid-progress) and `Main` (5 tasks covering every card state) view models. Extracted the card DataTemplate into `TaskCard.axaml` (+ thin code-behind) and the bottom footer (HF-gate box + status strip) into `QueueFooter.axaml` (+ thin code-behind) — moved XAML is byte-identical, only `Grid.Row=\"2\"` attributes stripped. Wired `Design.*` attached properties on `QueuePanel.axaml`, `TaskCard.axaml`, `QueueFooter.axaml`, `MainWindow.axaml`, and added `Design.PreviewWith` to `VisualRelayTheme.axaml`. Updated `ContrastTests.cs` to scan all three panel XAML files (`QueuePanel.axaml`, `TaskCard.axaml`, `QueueFooter.axaml`) and renamed methods from `QueuePanel_*` to `PanelXamls_*`. Updated `StatusFooterFlyoutTests.cs` to resolve `QueueFooter` via `GetVisualDescendants` before `FindControl`. Added `DesignDataTests.cs` (3 plain `[Fact]`s covering card-state coverage, SelectedTask null guard, and Card mid-progress properties) and `QueuePanelSplitRenderTests.cs` (2 `[AvaloniaFact]`s proving standalone rendering from DesignData). All files stay ≤300 lines. `Main.SelectedTask` deliberately left null to avoid `SelectTaskAsync` disk I/O polluting previews. `TaskCard` root has no `Background`, preserving the transparency chain for `TaskCardRenderTests`. All 3023 tests pass (0 failures).",
  "commitMessages": [
    "feat: add DesignData class and split QueuePanel into TaskCard and QueueFooter controls",
    "feat: wire design-time DataContext for previewable queue cards in all panel XAML files",
    "refactor: extract card template and footer from QueuePanel to stay under 300-line guard",
    "test: add DesignDataTests and QueuePanelSplitRenderTests; update ContrastTests and StatusFooterFlyoutTests"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

