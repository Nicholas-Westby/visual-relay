## Stage 1 - Ideate

{
  "summary": "Display user home paths with tilde (~) prefix in the GUI instead of the full absolute path (/Users/username/...). Keep full paths internally. Apply on macOS and Windows where applicable.",
  "options": [
    "Option A — Dedicated static helper + extension method. Add a `PathDisplayHelper` static class in VisualRelay.App (or VisualRelay.Core) with a method like `string ToDisplayPath(this string absolutePath)` that checks `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` on macOS (and `%USERPROFILE%` on Windows), replaces the prefix with `~`, and leaves non-home paths unchanged. Then update all GUI path-display call sites (XAML bindings, ViewModels, code-behind labels) to pass through this helper. This is the simplest, most testable approach and keeps the display logic in one place.",
    "Option B — IValueConverter for XAML bindings. Create an `HomePathToTildeConverter : IValueConverter` that does the replacement in `Convert`. Apply it in XAML bindings wherever a path is displayed (e.g., `{Binding SomePath, Converter={StaticResource HomePathToTildeConverter}}`). On Windows, handle both forward-slash and backslash paths. This keeps the transformation declarative and avoids touching ViewModels, but requires finding every relevant XAML binding.",
    "Option C — ViewModel-level computed property. For each ViewModel that exposes a full path (e.g., `FullPath`), add a read-only `DisplayPath` property that applies the tilde substitution and bind the UI to `DisplayPath` instead. On Windows, check `Environment.SpecialFolder.UserProfile`. This keeps the concern in the presentation layer and is easy to unit-test, but requires more per-ViewModel boilerplate."
  ]
}

## Stage 2 - Research

{
  "findings": "The Visual Relay GUI displays full absolute paths in four locations. (1) TopBar.axaml:60-67 shows `RootParentPath` (e.g., `/Users/nicholaswestby/Dev`) next to the folder/root-name, computed by `RootFolderDisplay.Parent()` at ViewModels/RootFolderDisplay.cs:25-34 which trims directory separators and returns `Path.GetDirectoryName`. (2) QueuePanel.axaml:83-89 shows `MarkdownPath` per task row, exposing `RelayTaskItem.MarkdownPath` via `TaskRowViewModel.MarkdownPath`. (3) TaskDetailPanel.axaml:246 shows `AttachmentRowViewModel.Path` for each attachment. (4) `StageRowViewModel.RevealTarget` (line 252) is used for file-reveal but not displayed as text. The screenshot confirms full `/Users/nicholaswestby/Dev/visual-relay` style paths in the top bar. The tilde concept already exists in `RelayConfigLoader.cs:142-149` which expands `~` and `$HOME` in config paths. An `IValueConverter` precedent exists in `BoolNotConverter.cs` with a singleton `Instance` pattern referenced in XAML via `{x:Static controls:BoolNotConverter.Instance}`. `Environment.SpecialFolder.UserProfile` is used throughout (RelayConfigLoader.cs, MxcPolicyGenerator.cs, ProcessRunners.SandboxEnv.cs) and works cross-platform. The `RootFolderDisplay` static class is the natural home for a tilde-replacement helper. The three implementation strategies from ideation all remain viable — Option A (static helper + call-site updates via `RootFolderDisplay`) would be cleanest since it already centralizes root-path display logic, while Option B (IValueConverter) fits the existing converter pattern but would need a new converter class plus XAML binding updates at each of the 3-4 sites. Option C (ViewModel computed properties) would spread tilde logic across `MainWindowViewModel.Properties.cs`, `TaskRowViewModel.cs`, and `AttachmentRowViewModel.cs`. On Windows, `Environment.SpecialFolder.UserProfile` resolves to `C:\\Users\\<username>` which maps naturally to `~` as well.",
  "constraints": [
    "The full absolute path must be kept in-memory and on-disk unchanged; only the GUI display text should show a tilde-shortened form.",
    "The change must handle both macOS paths (`/Users/username/...`) and Windows paths (`C:\\Users\\username\\...`), with `~` being the appropriate shorthand on both platforms.",
    "RootFolderDisplay.Name(), RootFolderDisplay.Parent(), and RootFolderDisplay.DefaultPath() in ViewModels/RootFolderDisplay.cs should not be modified to return tilde-prefixed strings since Parent() and DefaultPath() serve internal/storage purposes, not just display.",
    "Any converter or helper must be unit-testable — the existing `BoolNotConverter` pattern shows a stateless singleton is preferred.",
    "XAML bindings use `{x:Static ...Converter.Instance}` referencing pattern for converters, established in BoolNotConverter.cs, StageInputView.axaml, SettingsPanel.axaml, and QueuePanel.axaml.",
    "Attachment path binding at TaskDetailPanel.axaml:246 (`Text=\"{Binding Path}\"`) binds to the `Path` property of `AttachmentRowViewModel` which is a simple property — no computed property or converter is currently applied.",
    "The `RootParentPath` property in MainWindowViewModel.Properties.cs:19 directly delegates to `RootFolderDisplay.Parent(RootPath)` — any tilde conversion must be inserted between them or in a converter.",
    "MarkdownPath in TaskRowViewModel.cs:33 delegates directly to `Task.MarkdownPath` — no display transformation layer exists today.",
    "The VisualRelay.App project targets Avalonia UI (not WPF/WinForms) and uses CommunityToolkit.Mvvm for observable properties — the converter must implement `Avalonia.Data.Converters.IValueConverter` (from Avalonia), not System.Windows.Data.",
    "Windows paths may use backslash separators; the helper must normalize or handle both `Path.DirectorySeparatorChar` and `Path.AltDirectorySeparatorChar`.",
    "The change should not affect the `RevealTarget` path in StageRowViewModel.cs:252 which is only passed to `FileReveal.Reveal()` and not displayed as text in XAML.",
    "Tests exist in tests/VisualRelay.Tests/ and any new helper/converter should have corresponding unit tests."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The GUI renders full absolute home paths (e.g. /Users/nicholaswestby/Dev) at three TextBlock bindings — TopBar.axaml:60-67 (RootParentPath), QueuePanel.axaml:83-89 (MarkdownPath), and TaskDetailPanel.axaml:246 (Attachment.Path). No tilde-shortening helper, converter, or computed property exists anywhere in the codebase. The screenshot viewed in stage 2 turn 12 visually confirms the raw path display. RootFolderDisplay.Parent() returns Path.GetDirectoryName with no ~ substitution. Both MarkdownPath and Attachment.Path are raw passthroughs with zero transformation. Precedent infrastructure exists: BoolNotConverter.cs demonstrates the Avalonia IValueConverter singleton pattern, Environment.SpecialFolder.UserProfile is already used throughout, and RelayConfigLoader.cs already does ~ expansion (input-side only).",
  "excerpts": [
    "[stage2 turn 13 think] The screenshot shows the top bar… I can see a path displayed like '/Users/nicholaswestby/Dev/visual-relay' in the top bar. This confirms that the full absolute user home path is being shown directly in the GUI, exactly the issue the task wants to fix.",
    "[stage2 turn 20 grep ToDisplayPath|ToTilde|ToHomeRelative] Found 0 matches in src/ — no tilde display helper exists.",
    "[RootFolderDisplay.cs:25-34] public static string Parent(string rootPath) { … return Path.GetDirectoryName(trimmed) ?? trimmed; } — no ~ substitution.",
    "[MainWindowViewModel.Properties.cs:19] public string RootParentPath => RootFolderDisplay.Parent(RootPath); — raw delegation.",
    "[TaskRowViewModel.cs] MarkdownPath delegates directly to Task.MarkdownPath — no display transformation.",
    "[AttachmentRowViewModel.cs:5,8] public sealed class AttachmentRowViewModel(string path, …) { public string Path { get; } = path; } — raw passthrough.",
    "[TopBar.axaml:60-67] TextBlock Text=\"{Binding RootParentPath}\" — no converter applied.",
    "[QueuePanel.axaml:83-89] TextBlock Text=\"{Binding MarkdownPath}\" — no converter applied.",
    "[TaskDetailPanel.axaml:246] TextBlock Text=\"{Binding Path}\" — no converter applied."
  ],
  "repro": "1. Launch Visual Relay on macOS with a project inside the user home directory (e.g. /Users/nicholaswestby/Dev/visual-relay). 2. Observe the top bar — the parent path reads '/Users/nicholaswestby/Dev' instead of '~/Dev'. 3. Observe the queue panel task rows — MarkdownPath shows the full absolute path. 4. Open task detail, add an attachment from the home directory — the attachment path shows the full absolute path."
}

## Stage 4 - Plan

{
  "plan": "Create an Avalonia IValueConverter (HomePathToTildeConverter) following the existing BoolNotConverter singleton pattern. It replaces the user's home directory prefix with ~ for GUI display only, keeping in-memory paths unchanged. Apply the converter to the three XAML TextBlock bindings that show paths: TopBar.axaml RootParentPath, QueuePanel.axaml MarkdownPath, and TaskDetailPanel.axaml attachment Path. Add unit tests covering macOS/Windows home paths, non-home paths, edge cases, and null.",
  "manifest": [
    "+src/VisualRelay.App/Views/Controls/HomePathToTildeConverter.cs",
    "+tests/VisualRelay.Tests/HomePathToTildeConverterTests.cs",
    "src/VisualRelay.App/Views/Controls/TopBar.axaml",
    "src/VisualRelay.App/Views/Controls/QueuePanel.axaml",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml"
  ]
}

> **Worktree filter (stage 5)**: discarded untracked deleted: 1.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/HomePathToTildeConverterTests.cs"
  ],
  "rationale": "Created 15 xUnit [Fact] tests covering: home path replacement (~/rest), exact home (~), trailing separators, different-user home (unchanged), outside home (unchanged), null, empty string, case-difference (unchanged), relative paths (unchanged), paths that start-with-home-but-are-longer-dirname (unchanged), ConvertBack pass-through, and singleton identity. A stub HomePathToTildeConverter.cs was created so the tests compile. Targeted test command 'sh tools/dotnet-test-files.sh tests/VisualRelay.Tests/HomePathToTildeConverterTests.cs' confirmed 4 tilde-replacement tests fail (expected, stub is pass-through) while 11 pass-through tests pass."
}

## Stage 6 - Implement

{
  "summary": "Created HomePathToTildeConverter.cs (singleton IValueConverter following BoolNotConverter pattern) that replaces the user's home directory prefix with ~ for GUI display. Applied the converter to three XAML bindings: TopBar.axaml RootParentPath, QueuePanel.axaml MarkdownPath, and TaskDetailPanel.axaml attachment Path. All 15 unit tests pass (0 failures). No ViewModel or internal path storage was modified — only display text is affected."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified HomePathToTildeConverter and its XAML bindings: all 15 unit tests pass (0 failures). Converter replaces user home path prefix with ~ for GUI display in TopBar, QueuePanel, and TaskDetailPanel — internal paths unchanged." }

## Stage 9 - Verify

{
  "summary": "HomePathToTildeConverter (singleton IValueConverter) replaces the user's home path prefix with `~` in three XAML display bindings (TopBar RootParentPath, QueuePanel MarkdownPath, TaskDetailPanel attachment Path). All 15 unit tests pass; the 5 failures in the full suite are pre-existing Installer5DocsTests (README checks) unrelated to this change.",
  "commitMessages": [
    "feat: add HomePathToTildeConverter to display ~/ for user home paths in GUI",
    "feat: apply tilde-shortening converter to TopBar, QueuePanel, and TaskDetailPanel path bindings",
    "feat: replace absolute home paths with ~ prefix in three XAML display bindings"
  ]
}

## Stage 10 - Fix-verify (attempt 1/3)

{
  "summary": "Fixed 5 pre-existing Installer5DocsTests failures by updating README.md: changed install headings from H1 to H2 (## Install), added Determinate Nix/uv/nono prereq mentions in the install section, and added the init command to the Commands section. Full suite: 0 failed, 2221 passed, 22 skipped.",
  "amendManifest": ["README.md"]
}

## Stage 11 - Commit

Committed by Visual Relay.

