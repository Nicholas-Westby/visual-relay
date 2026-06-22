## Stage 1 - Ideate

{
  "summary": "The \"Set up empty project\" button's BootstrapProjectCommand has its CanExecute frozen at startup because neither _rootPath nor _isBusy carries [NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))], so the button stays disabled forever. Fix by adding that attribute to both fields, plus write a headless test that mirrors the real init order (construct without RootPath, show window, then set RootPath) to reproduce and validate the fix. Audit sibling commands for the same pattern.",
  "options": [
    "Option A — Add [NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))] to _rootPath and _isBusy (minimal idiomatic fix using CommunityToolkit source generators).",
    "Option B — Replace the inline CanExecute lambda with a computed [ObservableProperty]-driven bool property that notifies on RootPath/IsBusy changes, making the reactive dependency explicit.",
    "Option C — Call BootstrapProjectCommand.NotifyCanExecuteChanged() imperatively inside the RootPath and IsBusy setters (same effect, more visible but less idiomatic)."
  ]
}

## Stage 2 - Research

{
  "findings": "The bug is a stale-CanExecute freeze: `CanBootstrapProject()` reads `!IsBusy && Directory.Exists(RootPath)` but neither `_rootPath` nor `_isBusy` carries `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]`. The button binds at startup when RootPath is empty, evaluates to false once, and is never re-queried. The existing test hides this by setting RootPath at construction. Fix: add `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]` to both fields alongside their existing notify attributes. Sibling audit: `CanRefresh()` also misses `IsBusy` notification (though RootPath change already triggers RefreshCommand). No other commands share the exact stale-RootPath pattern.",
  "constraints": [
    "Do not edit files during Research stage — only investigate and report.",
    "Fix files: `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` — add `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]` to both `_rootPath` (after line 96) and `_isBusy` (after line 195).",
    "New test must construct VM without RootPath (default/empty), show window/RunJobs, then set RootPath, RunJobs, assert button `IsEffectivelyEnabled` is true.",
    "Existing test `ConfigInitEmptyStateUiTests` must remain green after the fix.",
    "Test file: `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs` — add the new test alongside the existing one, using `[Collection(\"Headless\")]`, `public sealed class`, `[AvaloniaFact]`.",
    "Keep `ConfigInitEmptyStateUiTests` as the host file (already registered in SplitGuardVerificationTests.Conventions.cs line 60 as a headless collection member).",
    "Button name in XAML: `BootstrapProjectCommand` bound to `QueuePanel.axaml` line 171, button content \"Set up empty project\".",
    "Do not alter `CanBootstrapProject()` logic, window layout, disabled styling, or any file outside the two identified (MainWindowViewModel.cs + the test file).",
    "The `init-panel-cropped.png` screenshot is not on disk; the screenshot gate may need a new capture after the fix."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Root cause: stale CanExecute freeze. `CanBootstrapProject()` reads `!IsBusy && Directory.Exists(RootPath)` but neither `_rootPath` (line 92-100) nor `_isBusy` (line 191-198) carries `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]`. `_rootPath` notifies RefreshCommand/ToggleArchiveCommand/RunSelectedCommand/DrainQueueCommand — BootstrapProjectCommand is absent. `_isBusy` notifies ToggleArchiveCommand/RunSelectedCommand/DrainQueueCommand/CreateConfigCommand/AddAttachmentsCommand — BootstrapProjectCommand is absent. No imperative `BootstrapProjectCommand.NotifyCanExecuteChanged()` call exists anywhere in source (grep confirmed zero matches). The private constructor sets `_rootPath = RootFolderDisplay.DefaultPath()` which resolves to `string.Empty` when `~/Dev/sample-tasks` doesn't exist (always in CI/headless). At window creation the button binds `{Binding BootstrapProjectCommand}`, the lazy [RelayCommand] property is first accessed, `CanExecute` evaluates `Directory.Exists(\"\")` = false → command is created disabled. Later setting `RootPath` to a valid directory never re-raises `CanExecuteChanged`, so the button remains disabled for the life of the process. The existing test `ConfigInitEmptyStateUiTests` hides the bug by setting `RootPath` in the object initializer (`new MainWindowViewModel { RootPath = repo.Root }`) before `Show()`/`RunJobs()` ever materialize the binding, so CanExecute evaluates true from the start. Sibling audit: `CanRefresh()` also reads `!IsBusy && Directory.Exists(RootPath)` — `_rootPath` notifies RefreshCommand ✓ but `_isBusy` does NOT notify RefreshCommand ✗ (same class of bug, lower severity).",
  "excerpts": [
    "MainWindowViewModel.cs:92-100 — _rootPath [NotifyCanExecuteChangedFor] lists RefreshCommand, ToggleArchiveCommand, RunSelectedCommand, DrainQueueCommand. BootstrapProjectCommand is MISSING.",
    "MainWindowViewModel.cs:191-198 — _isBusy [NotifyCanExecuteChangedFor] lists ToggleArchiveCommand, RunSelectedCommand, DrainQueueCommand, CreateConfigCommand, AddAttachmentsCommand. BootstrapProjectCommand is MISSING.",
    "MainWindowViewModel.Bootstrap.cs:8 — CanBootstrapProject() => !IsBusy && Directory.Exists(RootPath) — reads both fields, neither notifies the command.",
    "MainWindowViewModel.cs:60 — _rootPath = RootFolderDisplay.DefaultPath() — returns string.Empty in headless/CI, so Directory.Exists('') = false at construction.",
    "QueuePanel.axaml:171 — Button Command=\"{Binding BootstrapProjectCommand}\" Content=\"Set up empty project\" — no x:Name.",
    "ConfigInitEmptyStateUiTests.cs:23 — new MainWindowViewModel { RootPath = repo.Root } — object initializer sets RootPath before window.Show()/RunJobs(), so command is first accessed when Directory.Exists is already true, hiding the freeze.",
    "MainWindowViewModel.Helpers.cs:201 — CanRefresh() => !IsBusy && Directory.Exists(RootPath) — _rootPath notifies RefreshCommand ✓, _isBusy does NOT (sibling bug, lower severity)."
  ],
  "repro": "Construct MainWindowViewModel with default (empty) RootPath → Show MainWindow → RunJobs (button binds, CanExecute evaluates to false, command frozen disabled) → set vm.RootPath = repo.Root (existing directory) → RunJobs → assert bootstrap button IsEffectivelyEnabled is false (today, broken) or true (after fix: [NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))] on both _rootPath and _isBusy causes CanExecuteChanged to fire on RootPath set, button re-queries CanBootstrapProject(), finds Directory.Exists true, enables)."
}

## Stage 4 - Plan

{
  "plan": "Add [NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))] to both _rootPath (after line 96) and _isBusy (after line 196) in MainWindowViewModel.cs. This causes the CommunityToolkit source-generator to emit BootstrapProjectCommand.NotifyCanExecuteChanged() inside the generated setters for RootPath and IsBusy, so the button re-evaluates CanBootstrapProject() when the folder or busy-state changes. Write a headless test in ConfigInitEmptyStateUiTests.cs that constructs the VM without RootPath (default empty), shows the window + RunJobs (button binds while RootPath is empty, CanExecute=false), then sets RootPath to an existing directory + LoadInitialAsync + RunJobs, and asserts the bootstrap button's IsEffectivelyEnabled is true. This test fails today (button stuck disabled) and passes after the attributes are added. Keep the existing test in the same file green.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs"
  ],
  "rationale": "Added BootstrapButton_EnablesAfterRootPathSet_WhenMirroringRealInitOrder to the existing ConfigInitEmptyStateUiTests class. The test mirrors the real startup order: constructs MainWindowViewModel with default (empty) RootPath, shows the window + RunJobs so the bootstrap button binding materializes while Directory.Exists(\"\") = false (CanExecute frozen disabled), then sets RootPath to an existing directory + LoadInitialAsync + RunJobs, and asserts the button's IsEffectivelyEnabled is true. The test fails today (button stuck disabled — CanExecute never re-evaluated) and will pass once [NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))] is added to both _rootPath and _isBusy in MainWindowViewModel.cs. The existing test InitEmptyState_TypingAndClickingThroughControls_WritesConfigAndFlipsVisibility remains green."
}

## Stage 6 - Implement

{ "summary": "Added [NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))] to _rootPath (line 97) and _isBusy (line 198) in MainWindowViewModel.cs. This causes the CommunityToolkit source-generator to emit BootstrapProjectCommand.NotifyCanExecuteChanged() when RootPath or IsBusy changes, so the button re-evaluates CanBootstrapProject() and enables as soon as RootPath points at an existing directory. Both tests in ConfigInitEmptyStateUiTests pass: the existing InitEmptyState_TypingAndClickingThroughControls_WritesConfigAndFlipsVisibility remains green, and the new BootstrapButton_EnablesAfterRootPathSet_WhenMirroringRealInitOrder passes, confirming the frozen-CanExecute bug is fixed." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified fix: both tests pass (2 passed, 0 failed, 0 skipped). The `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]` attributes on `_rootPath` (line 97) and `_isBusy` (line 198) in `MainWindowViewModel.cs` cause `CanExecuteChanged` to fire when `RootPath` or `IsBusy` changes, unfreezing the button's `CanExecute`. The new test `BootstrapButton_EnablesAfterRootPathSet_WhenMirroringRealInitOrder` mirrors the real startup order (VM constructed with empty `RootPath`, window shown, then `RootPath` set) and confirms `IsEffectivelyEnabled` is `true`." }

## Stage 9 - Verify

{
  "summary": "Added `[NotifyCanExecuteChangedFor(nameof(BootstrapProjectCommand))]` to `_rootPath` and `_isBusy` in MainWindowViewModel.cs, fixing the stale-CanExecute freeze that kept the \"Set up empty project\" button permanently disabled. The button now re-evaluates `CanBootstrapProject()` when either `RootPath` or `IsBusy` changes. Added a headless test mirroring the real init order (VM constructed with empty RootPath, window shown, then RootPath set) that reproduces the bug and validates the fix. Both tests pass.",
  "commitMessages": [
    "fix: add NotifyCanExecuteChangedFor(BootstrapProjectCommand) to _rootPath and _isBusy",
    "fix: unfreeze 'Set up empty project' button's CanExecute so it enables after folder is opened",
    "test: add test reproducing stale-CanExecute bug with real init order (empty RootPath at construction)"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

