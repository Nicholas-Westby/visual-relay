## Stage 1 - Ideate

{
  "summary": "Fix scrollbar bouncing in the LLM COMMANDS ListBox (ActivityColumn.axaml) caused by Avalonia's default VirtualizingStackPanel estimating scroll extent from variable-height trace cards. Swap to a non-virtualizing StackPanel (matching the StageBoard pattern), or alternatively cap card heights with MaxLines. The bounded trace-set size (tens of cards per session filter) makes de-virtualization safe.",
  "options": [
    "De-virtualize LLM COMMANDS ListBox with explicit <ListBox.ItemsPanel><ItemsPanelTemplate><StackPanel/></ItemsPanelTemplate></ListBox.ItemsPanel> (apply similarly to RUN LOG if needed)",
    "Cap item card TextBlock height with MaxLines (e.g., 5) to bound/uniform heights, retaining virtualization for large lists",
    "Hybrid: de-virtualize LLM COMMANDS (severe bounce) + cap RUN LOG (minor bounce, keep virtualization)"
  ]
}

## Stage 2 - Research

{
  "findings": "ActivityColumn.axaml (139 lines, within 300-line limit) contains two ListBox controls. The LLM COMMANDS ListBox (lines 89-135) binds to TraceEntries (ObservableCollection<TraceEntry> in MainWindowViewModel.cs:59) and uses the default VirtualizingStackPanel. Its item template has a TextBlock (lines 123-130) with TextWrapping='Wrap' and no MaxLines, so TraceEntry.Content (TraceEntry.cs:15, an unbounded string) creates highly variable card heights — from one-line ToolCall to multi-line Thinking blocks. The RUN LOG ListBox (lines 39-64) binds to Events and also virtualizes, but its items are mostly single-line (DisplayLine has MaxLines=1, DetailLine is SelectableTextBlock without wrapping), so it bounces far less. StageBoard.axaml (lines 27-35) shows the existing non-virtualizing pattern: ScrollViewer + ItemsControl with explicit WrapPanel. Traces are prepended (inserted at index 0) via HandleRelayEvent in MainWindowViewModel.Helpers.cs:35-42, so live streaming could also shift the scrollbar — a confounder distinct from the virtualization bounce. The fix is presentation-only (no VM/converter changes expected). Avalonia 12.0.4 (confirmed in csproj) has no scroll-anchoring/height-caching for virtualizing variable-height lists.",
  "constraints": [
    "File size limit: 300 lines per .cs/.axaml file (enforced by tools/guards/check-file-size.sh); ActivityColumn.axaml is currently 139 lines, leaving ~160 lines of headroom",
    "No view-model or converter logic changes expected — this is presentation-only; if any are added, they must be covered test-first",
    "Must pass ./visual-relay check: format verification (dotnet format --verify-no-changes), build, tests, and screenshot generation must all succeed",
    "Conventional Commit format required for the commit message",
    "Must justify the chosen approach (de-virtualize vs cap MaxLines vs hybrid) against trace-set size in the commit/PR body",
    "RUN LOG should be considered but can be left virtualized since its items are mostly uniform-height (MaxLines=1 on title, no wrapping on details); the bounce is minimal there",
    "Live trace insertion (newest-first prepend during streaming) is a confounder that can also shift the scrollbar — the panel change fixes the manual-scroll bounce regardless, but should be noted separately",
    "Avalonia 12.0.4 has no built-in scroll-anchoring or height-caching to mitigate variable-height virtualization"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The LLM COMMANDS ListBox (ActivityColumn.axaml:89-135) has no explicit ItemsPanel, so Avalonia 12.0.4 (VisualRelay.App.csproj:17) uses its default VirtualizingStackPanel. The item template's content TextBlock (lines 123-130) has TextWrapping='Wrap' with no MaxLines, rendering TraceEntry.Content (TraceEntry.cs:15, an unbounded string) with highly variable heights — from a one-line ToolCall to multi-paragraph Thinking/AssistantText blocks. The screenshot (visual-relay-main.png) confirms this visually: a ToolResult card spans ~10+ wrapped lines while adjacent ToolCall cards are a single line. A virtualizing panel estimates total scroll extent from the average height of only realized (visible) items; as new taller/shorter cards get realized during scrolling, the estimate changes and the scrollbar thumb jumps and resizes — the bounce. The RUN LOG ListBox (lines 39-64) also virtualizes but its items are uniform: DisplayLine has MaxLines='1', DetailLine is a SelectableTextBlock without wrapping, so the bounce there is minimal. The StageBoard (StageBoard.axaml:27-35) deliberately avoids this with a non-virtualizing WrapPanel inside a ScrollViewer — the project's own pattern for stable scrolling. A confounder exists: live trace insertion during streaming prepends items at index 0 (MainWindowViewModel.Helpers.cs:35-42, Insert(0, ...)), which can independently shift scroll position.",
  "excerpts": [
    "ActivityColumn.axaml:89-91 — LLM COMMANDS ListBox with no ItemsPanel: `<ListBox Grid.Row='1' Margin='12' ItemsSource='{Binding TraceEntries}'>`",
    "ActivityColumn.axaml:123-130 — Unbounded content TextBlock: `<TextBlock Grid.Row='1' Text='{Binding Content}' ... TextWrapping='Wrap'/>` (no MaxLines)",
    "TraceEntry.cs:12-15 — Content is an unbounded string: `public sealed record TraceEntry(TraceEntryKind Kind, string Title, string Content, int? StageNumber = null)`",
    "StageBoard.axaml:27-35 — Existing non-virtualizing pattern: `<ScrollViewer ...> <ItemsControl ...> <ItemsControl.ItemsPanel> <ItemsPanelTemplate> <WrapPanel/> </ItemsPanelTemplate> </ItemsControl.ItemsPanel>`",
    "VisualRelay.App.csproj:17 — Avalonia 12.0.4 confirmed (no scroll-anchoring/height-caching in this version)",
    "MainWindowViewModel.Helpers.cs:35-42 — Live insertion confounder: `TraceEntries.Insert(0, traceEntry)` and `Events.Insert(0, relayEvent)` prepend during streaming",
    "ActivityColumn.axaml:39-64 — RUN LOG ListBox also virtualizes but has uniform-height items: `MaxLines='1'` on DisplayLine, SelectableTextBlock without wrapping for DetailLine"
  ],
  "repro": "1. Launch the app with `./visual-relay launch`. 2. Select a task that has been run (so LLM COMMANDS has trace entries). 3. Observe the LLM COMMANDS pane: cards have visibly different heights (ToolCall cards are ~1 line, Thinking/AssistantText cards are ~5-15 lines). 4. Scroll via mouse wheel or drag the scrollbar thumb — the thumb jumps in size/position as cards with different heights are realized/virtualized. 5. Compare with the RUN LOG above: its scrollbar tracks smoothly because all rows are uniform height. 6. For the live-insertion confounder: start a new run and watch the LLM COMMANDS pane — new cards being prepended at the top can independently shift the scroll position."
}

## Stage 4 - Plan

{
  "plan": "## Concrete Plan: De-virtualize LLM COMMANDS ListBox\n\n### Why this approach\n\nDe-virtualize the LLM COMMANDS list. The trace set is bounded: a stage filter/latest-attempt selection keeps the visible session to tens of cards, so realizing every item is fine. The alternative (capping `MaxLines` on the content `TextBlock`) would hide content from the user, requiring extra hover/expand interaction. De-virtualizing is simpler, preserves full visibility, and matches the existing `StageBoard.axaml` pattern (ScrollViewer + non-virtualizing panel).\n\n### What changes\n\n**Edit `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml`** (currently 139 lines → **144 lines** after edit, well under 300):\n\nInsert an explicit `<ListBox.ItemsPanel>` on the **LLM COMMANDS `ListBox`** (between line 91 `ItemsSource=\"{Binding TraceEntries}\">` and line 92 `<ListBox.ItemTemplate>`), replacing the default `VirtualizingStackPanel` with a plain non-virtualizing `StackPanel`:\n\n```xml\n         <ListBox Grid.Row=\"1\"\n                  Margin=\"12\"\n                  ItemsSource=\"{Binding TraceEntries}\">\n+          <ListBox.ItemsPanel>\n+            <ItemsPanelTemplate>\n+              <StackPanel/>\n+            </ItemsPanelTemplate>\n+          </ListBox.ItemsPanel>\n           <ListBox.ItemTemplate>\n```\n\nThe `ListBox` keeps its built-in `ScrollViewer`; only the items panel changes. Scroll extent is now the true total height of all realized cards, so the scrollbar thumb stays proportional and stable during wheel/drag.\n\n### What does NOT change\n\n- **RUN LOG ListBox** (lines 39–64): left virtualized. Its items have `MaxLines=\"1\"` on the title and no `TextWrapping` on the detail line, so heights are uniform — the virtualization estimate is accurate and the bounce is negligible.\n- **No view-model or converter logic** is added, so no new unit tests are needed.\n\n### Confounder noted (separate issue)\n\nLive trace insertion during streaming (`MainWindowViewModel.Helpers.cs:40` — `TraceEntries.Insert(0, traceEntry)`) prepends cards at the top of the list, which can independently shift the scroll viewport. This is a distinct behavior from the virtualization bounce; the panel change fixes the **manual-scroll** bounce regardless. If auto-scroll displacement during live runs is also problematic, it should be tracked separately (e.g., scroll-anchoring on prepend).\n\n### Verification\n\n1. `./visual-relay build` — must compile cleanly.\n2. `./visual-relay screenshot` — generates updated screenshot; verify LLM COMMANDS looks correct.\n3. `./visual-relay launch` — manually scroll the LLM COMMANDS pane with wheel and scrollbar drag; confirm thumb stays stable and proportional, no jumping or resizing.\n4. Confirm scrolling reaches first and last card.\n5. `./visual-relay check` — full gate: format, build, tests, screenshot all green.\n\n### Commit\n\nConventional Commit: `fix: de-virtualize LLM COMMANDS list to stop scrollbar bounce`\n\nBody explains: root cause (virtualizing panel with variable-height cards), why de-virtualization is safe (bounded trace-set size), why RUN LOG is left unchanged, and notes the live-insertion confounder.",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/ActivityColumn.axaml"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [],
  "rationale": "This stage is a pure XAML presentation change: adding an explicit non-virtualizing `<ItemsPanelTemplate><StackPanel/></ItemsPanelTemplate>` to the LLM COMMANDS `ListBox` in `ActivityColumn.axaml`. No view-model, converter, or other C# logic is being added or modified, so there is no behavior to cover with a unit test. The task brief itself states: \"This is presentation-only; no view-model logic changes are expected, so there is no unit test to write first — but if any VM/converter logic is added, cover it test-first.\" The plan confirms: \"No view-model or converter logic is added, so no new unit tests are needed.\" Verification will be via manual scroll testing and `./visual-relay screenshot`."
}

## Stage 6 - Implement

{ "summary": "Added non-virtualizing StackPanel ItemsPanel to the LLM COMMANDS ListBox in ActivityColumn.axaml (lines 92-96), replacing the default VirtualizingStackPanel. This stops the scrollbar thumb from jumping/resizing during scroll because the true content height is now measured directly instead of estimated from visible items. RUN LOG was left virtualized (uniform-height items). All gates pass: build, 126 tests, format, screenshots. File: 144 lines." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified the LLM COMMANDS ListBox fix: non-virtualizing StackPanel ItemsPanel added at ActivityColumn.axaml:92-96. This stops scrollbar bouncing because the ScrollViewer now measures true content height instead of estimating from variable-height virtualized items. RUN LOG left virtualized (uniform-height items). All gates green: file under 300 lines, format clean, build succeeds, 126 tests pass, screenshots generated. The live-insertion confounder (TraceEntries.Insert at index 0 during streaming) is noted separately." }

## Stage 9 - Verify

{
  "summary": "De-virtualized the LLM COMMANDS ListBox in ActivityColumn.axaml by adding an explicit non-virtualizing StackPanel ItemsPanel (lines 92-96). This stops scrollbar bouncing caused by Avalonia's default VirtualizingStackPanel estimating scroll extent from variable-height trace cards. The trace set is bounded (tens of cards per session filter), making de-virtualization safe. RUN LOG left virtualized (uniform-height items, negligible bounce). Live-insertion confounder noted separately. All gates green: 144-line file, format clean, 126 tests pass, screenshots generated.",
  "commitMessage": "fix: de-virtualize LLM COMMANDS list to stop scrollbar bounce\n\nThe LLM COMMANDS ListBox used the default VirtualizingStackPanel, which\nestimates total scroll extent from the average height of realized items.\nTrace cards have highly variable heights (one-line ToolCall to multi-line\nThinking/AssistantText blocks), so each new card realized during scrolling\nrecomputes the estimate — causing the scrollbar thumb to jump and resize\n(the \"bounce\").\n\nReplace the default panel with an explicit non-virtualizing StackPanel.\nThe ListBox retains its built-in ScrollViewer, so scrolling still works,\nbut the extent is now the true total height and the thumb stays stable.\n\nWhy de-virtualize (vs. capping MaxLines): The trace set is bounded — a\nstage filter / latest-attempt selection keeps the visible session to tens\nof cards. De-virtualizing preserves full card content visibility without\nrequiring hover/expand interaction, and matches the existing StageBoard\npattern (ScrollViewer + non-virtualizing panel).\n\nRUN LOG left virtualized: its items are mostly uniform-height (MaxLines=1\non title, no TextWrapping on detail), so its virtualization estimate is\nstable and the bounce is negligible.\n\nNote: Live trace insertion (TraceEntries.Insert(0, ...) during streaming)\ncan independently shift scroll position — that confounder is distinct from\nthe virtualization bounce fixed here.\n\nCloses: llm-commands-scrollbar-bounces-on-scroll"
}

## Stage 10 - Fix-verify

{
  "summary": "Verified the LLM COMMANDS ListBox de-virtualization fix in ActivityColumn.axaml (lines 92-96): non-virtualizing StackPanel ItemsPanel replaces the default VirtualizingStackPanel. All gates pass — 144-line file, build 0/0, 126 tests pass, format clean, screenshots generated. The scrollbar thumb now tracks true content height instead of a variable-height virtualization estimate, stopping the bounce. RUN LOG left virtualized (uniform-height items). Live-insertion confounder noted separately."
}

## Stage 11 - Commit

Committed by Visual Relay.

