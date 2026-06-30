## Stage 1 - Ideate

{ "summary": "Improve Obsidian Bridge UX by adding a cross-platform Reveal-in-Finder/Explorer button (centralized so other panels reuse it), adding a \"60\" label to the input, and wrapping folder-name mentions in quotes. The codebase is a .NET 10 Avalonia UI app with XAML views and a Buttons/ control namespace under Views/Controls/Buttons/.", "options": ["A — Inline: add Reveal button + label + quotes directly in Obsidian Bridge panel only (fast, duplicates platform logic if reused elsewhere)", "B — Centralized PlatformRevealService + reusable RevealButton control; refactor Obsidian Bridge + any existing open-folder buttons to use it; add label and quotes separately", "C — Unified static RevealInExplorerCommand (ReactiveUI/CommunityToolkit) that any button can bind to; single edit pass for label and quotes"] }

## Stage 2 - Research

{
  "findings": "The codebase is a .NET 10 Avalonia UI app using CommunityToolkit.Mvvm for MVVM. The Obsidian Bridge settings panel lives in `src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml` (code-behind at `.axaml.cs`). It contains: a CheckBox for enabling the bridge; a descriptive TextBlock reading \"Files in New Tasks/ become tasks; summaries publish to Completed/\"; a three-column Grid (widths `*,Auto,100`) with a vault-root path TextBox (col 0), a Browse button bound to `BrowseVaultRootCommand` (col 1), and a naked poll-seconds TextBox bound to `ObsidianPollSeconds` (col 2) — no label at all on that third column. The default value of `ObsidianPollSeconds` is 60 (line 20 of `MainWindowViewModel.ObsidianBridge.cs`).\n\nA cross-platform `FileReveal` static class already exists in `src/VisualRelay.Core/Execution/FileReveal.cs` (with unit tests in `tests/VisualRelay.Tests/FileRevealTests.cs`). It builds platform-appropriate commands: macOS `open -R <path>`, Windows `explorer /select,<path>`, Linux `xdg-open <directory>`. The method `FileReveal.Reveal(string path)` is the shared entry point. Three existing buttons already use it: (1) **SettingsPanel** \"Show in Finder\" → `RevealSettingsFileCommand` reveales the .env file; (2) **ActivityColumn** \"Reveal\" → `RevealStageArtifactsCommand` reveals the selected stage's report/trace dir; (3) **TaskDetailPanel** (Attachments tab) each attachment has a \"Reveal\" button bound to `AttachmentRowViewModel.RevealCommand`. All three use `CommonButton` from `VisualRelay.App.Views.Controls.Buttons`.\n\nThe ObsidianBridge ViewModel (`MainWindowViewModel.ObsidianBridge.cs`) already has `BrowseVaultRootAsync()` (picks a folder via `IFolderPicker`) but no `RevealVaultRootCommand`. The `FileReveal.Reveal()` method is the centralized reveal mechanism — no separate reusable RevealButton control exists, but the task's \"centralized functionality\" requirement is already met by `FileReveal` itself.\n\nTests exist for Obsidian bridge settings (`ObsidianBridgeVmPropertiesTests.cs`, `ObsidianBridgeVmTests.cs`), the SettingsPanel reveal button (`SettingsPanelUiTests.cs`), and `FileReveal` (`FileRevealTests.cs`, `RevealSettingsFileCommandTests.cs`). The test collection uses `[Collection(\"Headless\")]` for Avalonia UI tests.",
  "constraints": [
    "All three changes (Reveal button, label, quotes) must be made in the same `ObsidianSettings.axaml` file with corresponding ViewModel additions in `MainWindowViewModel.ObsidianBridge.cs`.",
    "The new Reveal button for the vault root must use the existing `FileReveal.Reveal()` centralized mechanism (not a new platform-specific launch).",
    "A new relay command (e.g. `RevealVaultRootCommand`) must be added to the ViewModel (following the `[RelayCommand]` pattern like `BrowseVaultRootAsync`) since no such command exists.",
    "The label on the poll-seconds TextBox should display \"60\" — the default value of `ObsidianPollSeconds` — as a visual unit/label (e.g. a TextBlock saying \"60\") next to the input in column 2 of the Grid.",
    "The existing TextBlock on line 14 of `ObsidianSettings.axaml` must have quotes added around the folder names, changing from `\"Files in New Tasks/ become tasks; summaries publish to Completed/\"` to `\"Files in \\\"New Tasks/\\\" become tasks; summaries publish to \\\"Completed/\\\"\"`.",
    "The existing Browse button pattern (CommonButton with appearance=\"Path\") should be followed for the new Reveal button for consistency.",
    "Any existing tests that verify the ObsidianSettings XAML structure may need updating after the XAML changes (new named control, changed label text).",
    "The Grid column definitions may need adjustment to accommodate the additional Reveal button and poll-seconds label (currently `*,Auto,100`)."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The Obsidian Bridge settings panel (ObsidianSettings.axaml) has three deficiencies matching the task requirements. (1) No Reveal button: the 3-column Grid (line 19: *,Auto,100) has a vault-root TextBox in col 0 and a Browse button in col 1, but no Reveal button to open the vault root in Finder/Explorer. The ViewModel has BrowseVaultRootAsync (MainWindowViewModel.ObsidianBridge.cs line 67) but no RevealVaultRootCommand — confirmed by grep returning zero matches for 'RevealVaultRoot' across src/ and tests/. Meanwhile, FileReveal.Reveal() (FileReveal.cs line 32) is the existing centralized mechanism already used by three other panels: SettingsPanel 'Show in Finder' button (SettingsPanel.axaml line 29, bound to RevealSettingsFileCommand), ActivityColumn 'Reveal' button (ActivityColumn.axaml line 28, bound to RevealStageArtifactsCommand), and TaskDetailPanel attachment Reveal buttons (bound to AttachmentRowViewModel.RevealCommand). All three use CommonButton. (2) No label on the poll-seconds input: the TextBox at column 2 has no accompanying TextBlock or label — the Research stage confirmed 'no label at all on that third column.' The default value of ObsidianPollSeconds is 60 (line 20 of MainWindowViewModel.ObsidianBridge.cs: 'private int _obsidianPollSeconds = 60'). (3) Folder names unquoted: the TextBlock at line 14 reads 'Files in New Tasks/ become tasks; summaries publish to Completed/' — the folder names 'New Tasks/' and 'Completed/' lack surrounding double quotes. The Grid column definitions (currently *,Auto,100) will need adjustment to accommodate a new Reveal button column and the poll-seconds label.",
  "excerpts": [
    "[run.log:139] Research findings: 'a three-column Grid (widths *,Auto,100) with a vault-root path TextBox (col 0), a Browse button bound to BrowseVaultRootCommand (col 1), and a naked poll-seconds TextBox bound to ObsidianPollSeconds (col 2) — no label at all on that third column.'",
    "[ObsidianSettings.axaml:14-17] TextBlock: 'Files in New Tasks/ become tasks; summaries publish to Completed/' — folder names not quoted.",
    "[ObsidianSettings.axaml:19-28] Grid ColumnDefinitions='*,Auto,100' — only 3 columns, no Reveal button, no label for poll-seconds.",
    "[MainWindowViewModel.ObsidianBridge.cs:20] 'private int _obsidianPollSeconds = 60;' — default value confirms '60' label requirement.",
    "[MainWindowViewModel.ObsidianBridge.cs:67-72] BrowseVaultRootAsync exists but no RevealVaultRootCommand — grep for 'RevealVaultRoot' returns zero matches in src/ and tests/.",
    "[FileReveal.cs:32-52] FileReveal.Reveal(string path) is the centralized cross-platform reveal mechanism — open -R on macOS, explorer /select on Windows, xdg-open on Linux.",
    "[run.log:139] Research confirms 3 existing buttons use FileReveal: SettingsPanel RevealSettingsFileCommand, ActivityColumn RevealStageArtifactsCommand, TaskDetailPanel RevealCommand — all via CommonButton.",
    "[run.log:139] 'The ObsidianBridge ViewModel already has BrowseVaultRootAsync() but no RevealVaultRootCommand.'",
    "[ledger.md:17] Constraint: 'The Grid column definitions may need adjustment to accommodate the additional Reveal button and poll-seconds label (currently *,Auto,100).'"
  ],
  "repro": "1. Open src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml — observe the 3-column Grid (line 19) has no Reveal button for the vault root, no label next to the poll-seconds TextBox in column 2, and the TextBlock on line 14 lacks quotes around 'New Tasks/' and 'Completed/'. 2. Open src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs — observe BrowseVaultRootAsync exists (line 67) but no RevealVaultRootCommand. 3. Run 'grep -r RevealVaultRoot src/ tests/' — zero results, confirming the command is absent. 4. Compare with any existing reveal button (e.g., SettingsPanel.axaml line 25-31 for 'Show in Finder' bound to RevealSettingsFileCommand) — they all use FileReveal.Reveal() behind a [RelayCommand] method."
}

## Stage 4 - Plan

{
  "plan": "## Summary\nThree changes to Obsidian Bridge UX, all in the settings panel:\n\n1. **Quotes around folder names** — update the descriptive TextBlock text.\n2. **Reveal button** — add a `CommonButton` with `Appearance=\"Path\"` bound to a new `RevealVaultRootCommand`, which calls the centralized `FileReveal.Reveal()`.\n3. **\"60\" label** — wrap the poll-seconds TextBox in a horizontal StackPanel with a TextBlock reading \"60\".\n\n## Step-by-step\n\n### A. ViewModel — new command (`MainWindowViewModel.ObsidianBridge.cs`)\n- Add `using VisualRelay.Core.Execution;`\n- Add a `[RelayCommand]` method:\n```csharp\n[RelayCommand]\nprivate void RevealVaultRoot()\n{\n    if (!string.IsNullOrWhiteSpace(ObsidianVaultRoot))\n        FileReveal.Reveal(ObsidianVaultRoot);\n}\n```\nThis mirrors the existing `RevealSettingsFile()` pattern in `MainWindowViewModel.Settings.cs` line 84.\n\n### B. XAML view (`ObsidianSettings.axaml`)\n- **Line 14**: Change `Text=\"...New Tasks/...Completed/\"` to `Text=\"...&quot;New Tasks/&quot;...&quot;Completed/&quot;\"` (XML-escaped double quotes).\n- **Line 19**: Change `ColumnDefinitions=\"*,Auto,100\"` to `ColumnDefinitions=\"*,Auto,Auto,100\"`.\n- **After the Browse button** (new element at `Grid.Column=\"2\"`):\n  ```xml\n  <buttons:CommonButton Grid.Column=\"2\" Content=\"Reveal\"\n                        Appearance=\"Path\"\n                        Command=\"{Binding RevealVaultRootCommand}\"/>\n  ```\n- **Replace the poll-seconds TextBox** at Grid.Column=\"3\" (was Column=\"2\") with:\n  ```xml\n  <StackPanel Grid.Column=\"3\" Orientation=\"Horizontal\"\n              VerticalAlignment=\"Center\" Spacing=\"4\">\n    <TextBlock Text=\"60\" FontSize=\"11\" Foreground=\"#9AA3B1\"\n               VerticalAlignment=\"Center\"/>\n    <TextBox Text=\"{Binding ObsidianPollSeconds, Mode=TwoWay}\"\n             FontSize=\"12\" VerticalAlignment=\"Center\" Width=\"50\"/>\n  </StackPanel>\n  ```\n  (Width=\"50\" keeps the input compact since it no longer relies on the column's fixed 100px width.)\n\n### C. Test file — new (`tests/VisualRelay.Tests/RevealVaultRootCommandTests.cs`)\n- Instantiate `MainWindowViewModel`, set `ObsidianVaultRoot` to a temp directory path, assert `RevealVaultRootCommand.CanExecute(null)` is true, and verify the `FileReveal.BuildCommand` output matches expectations per platform (following `RevealSettingsFileCommandTests` pattern).",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs",
    "src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml",
    "+tests/VisualRelay.Tests/RevealVaultRootCommandTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RevealVaultRootCommandTests.cs"
  ],
  "rationale": "Five tests written for the target behavior:\n\n1. **RevealVaultRootCommand_CanExecute_WhenVaultRootIsSet** — verifies the generated `[RelayCommand]` property exists and is executable when `ObsidianVaultRoot` has a path (line 19 references the not-yet-existing `RevealVaultRootCommand`).\n\n2. **RevealVaultRootCommand_CanExecute_EvenWhenVaultRootIsEmpty** — verifies the command remains executable even with an empty root (the guard is in the method body, matching the existing `RevealSettingsFile`/`BrowseVaultRootAsync` pattern — no dedicated `CanExecute` method).\n\n3–5. **VaultRoot_BuildCommand_On{MacOs,Windows,Linux}** — verify the centralized `FileReveal.BuildCommand` produces the correct OS-specific reveal command for vault-root-like paths (open -R on macOS, explorer /select on Windows, xdg-open on Linux). These mirror the existing `FileRevealTests` and `RevealSettingsFileCommandTests` patterns.\n\nAll five tests compile (the BuildCommand ones) or fail-to-compile (the two RevealVaultRootCommand ones) because `MainWindowViewModel` has no `RevealVaultRootCommand` property yet — exactly the expected pre-implementation failure."
}

## Stage 6 - Implement

{
  "summary": "Implemented three Obsidian Bridge UX improvements: (1) Added RevealVaultRootCommand relay command in MainWindowViewModel.ObsidianBridge.cs that calls the centralized FileReveal.Reveal(), (2) Updated ObsidianSettings.axaml with quoted folder names, a new CommonButton Reveal button at Grid.Column=2 with Appearance=Path, and a 60 label next to the poll-seconds TextBox in a StackPanel at Grid.Column=3, (3) Fixed the Linux BuildCommand test expectation to match FileReveal's GetDirectoryName behavior. All 5 targeted tests pass."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All three Obsidian Bridge UX improvements already implemented and verified: (1) quoted folder names in the descriptive TextBlock, (2) centralized Reveal button bound to RevealVaultRootCommand using FileReveal.Reveal(), (3) \"60\" label next to the poll-seconds input. Grid expanded from 3 to 4 columns. All 5 targeted tests pass." }

## Stage 9 - Verify

{
  "summary": "Obsidian Bridge settings panel improved with (1) a centralized Reveal button (FileReveal.Reveal()) that opens the vault root in Finder/Explorer, (2) a \"60\" label beside the poll-seconds input, and (3) double-quoted folder names in the descriptive label. Grid expanded to 4 columns. All 2371 tests pass.",
  "commitMessages": [
    "feat(obsidian-bridge): add Reveal button to open vault in Finder/Explorer",
    "feat(obsidian-bridge): show default poll-interval value next to seconds input",
    "fix(obsidian-bridge): wrap folder names in quotes in descriptive label",
    "feat(obsidian-bridge): improve settings panel with reveal, label, and quoted paths"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

