## Stage 1 - Ideate

{
  "summary": "The HF-gate banner hint 'or open Settings ⚙ in the top bar' clips because it's laid out horizontally beside the 'Get a free token →' button in a fixed 280px QueuePanel — the combined width (326px) exceeds the ~250px content area. The fix is to move the TextBlock to its own line below the button with TextWrapping='Wrap', matching the sibling fix in init-panel-buttons-labels-clipped. Option A (stack vertically) is simplest and most robust; Option B (inline wrapping with MaxWidth binding) is fragile; Option C (Grid/DockPanel) is functionally similar to A but less idiomatic.",
  "options": [
    "Option A: Move the hint TextBlock to its own line below the button in the enclosing vertical StackPanel, add TextWrapping='Wrap' — mirrors the accepted init-panel fix, no new bindings, robust to longer text.",
    "Option B: Keep horizontal layout but add TextWrapping='Wrap' to the hint and bind MaxWidth to subtract button+spacing from the parent Border's ActualWidth — fragile, requires a converter, and can still look cramped.",
    "Option C: Replace the horizontal StackPanel with a Grid/DockPanel (auto/* columns) and add TextWrapping='Wrap' to the hint — preserves horizontal adjacency in theory but at 280px still wraps, and is more verbose than Option A."
  ]
}

## Stage 2 - Research

{
  "findings": "The HF-gate banner at lines 199-228 of QueuePanel.axaml uses a horizontal StackPanel (lines 215-226) that places the 'Get a free token →' button next to the 'or open Settings ⚙ in the top bar' TextBlock. At the fixed 280px QueuePanel width with 14px padding, the combined width (~326px) exceeds available content width (~252px), causing the outer Border's ClipToBounds to cut ~61px (35%) of the hint. The fix is to move the TextBlock to its own StackPanel row below the button with TextWrapping='Wrap', mirroring the sibling init-panel-buttons-labels-clipped fix. A new headless-UI test is needed (in the style of InitPanelButtonsLayoutTests) that renders MainWindow with no HF_TOKEN, resolves the hint TextBlock, and asserts its Bounds fit within the 280px QueuePanel width. Existing HfGateBannerVisibilityTests cover VM logic only. Test infrastructure: AvaloniaFact + Collection(\"Headless\"), TestRepository, SettingsTestHelpers.SeedUserEnv(repo, \"\") to clear HF_TOKEN, LoadInitialAsync() to flip _keyStatesLoaded, then Show() the MainWindow.",
  "constraints": [
    "QueuePanel Width=280 is intentional — do NOT widen it.",
    "Button must remain unchanged (label='Get a free token →', Command=OpenGetKeyUrlCommand, CommandParameter=https://huggingface.co/settings/tokens).",
    "Existing tests (HfGateBannerVisibilityTests, KeySetupPanelUiTests, ConfigInitEmptyStateUiTests, InitPanelButtonsLayoutTests) must stay green.",
    "The hint TextBlock must be robust to longer/localized text — TextWrapping='Wrap' is required.",
    "The outer Border has ClipToBounds='True' which clips overflow — the fix must ensure the hint never overflows.",
    "Test must fail BEFORE the layout fix (i.e., written first / asserted with current broken layout) and pass after.",
    "Conventional commit required. ./visual-relay check must pass."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The HF-gate hint 'or open Settings ⚙ in the top bar' was laid out horizontally beside the 'Get a free token →' button in a non-wrapping StackPanel. At the QueuePanel's fixed 280 px width with 14 px Border padding, the combined width (~326 px = 146 px button + 8 px spacing + 172 px hint) exceeded the ~250 px content area by ~76 px. The outer Border's ClipToBounds='True' cut ~61 px (35%) of the hint, confirmed by headless Skia screenshots showing only '…Get a free token →  or open Settings ⚙' with 'in the top bar' missing.",
  "excerpts": [
    "MainWindow.axaml line 51: `<controls:QueuePanel Width=\"280\" …/>` — QueuePanel fixed at 280 px (intentional, not to be widened).",
    "QueuePanel.axaml lines 215-226 (before fix): `<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">` wrapping the Button and hint TextBlock — the horizontal layout caused the overflow.",
    "QueuePanel.axaml lines 215-223 (after fix): Button and hint TextBlock are direct vertical siblings in the outer StackPanel; hint gets `TextWrapping=\"Wrap\"` instead of `VerticalAlignment=\"Center\"`.",
    "HfGateHintLayoutTests.cs: headless-UI test seeds no HF_TOKEN, renders MainWindow, resolves hint TextBlock, asserts `TranslatePoint(Bounds.Width, 0, queuePanel).X ≤ 280` — fails before fix (~341 px), passes after."
  ],
  "repro": "Build the no-keys state: `EnvironmentAccessor` with `XDG_CONFIG_HOME` pointing to a dir whose `visual-relay/.env` has no `HF_TOKEN` line, `LoadInitialAsync()` so `ShowHfGate` flips true, show `MainWindow`. Resolve the TextBlock whose text starts with 'or open Settings' and check its right edge in QueuePanel coordinates — before fix it's ~341 px > 280 px panel width, after fix it wraps and stays ≤ 252 px."
}

## Stage 4 - Plan

{
  "plan": "## Concrete implementation plan\n\n### 1. Fix the HF-gate hint layout in QueuePanel.axaml (lines 199–225)\n\n**Current (broken):** The hint `TextBlock` is laid out horizontally beside the \"Get a free token →\" button inside a `<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">`. At the fixed 280 px QueuePanel width, the combined width (~326 px: 146 px button + 8 px spacing + 172 px hint) overflows the ~250 px content area, causing the outer `Border`'s `ClipToBounds=\"True\"` to cut ~61 px of the hint.\n\n**Fix:** Remove the inner horizontal `StackPanel`. Place the `Button` and the hint `TextBlock` as direct children of the outer vertical `StackPanel` (which already contains `HfGateMessage` and `HfPricingNote` above). Add `TextWrapping=\"Wrap\"` to the hint so it uses the full ~250 px content width and wraps instead of clipping. Remove `VerticalAlignment=\"Center\"` (irrelevant once the hint stacks vertically).\n\n**Diff (lines 215–226):**\n- Remove: `<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">` wrapper and its closing tag.\n- Change the hint `TextBlock`: remove `VerticalAlignment=\"Center\"`, add `TextWrapping=\"Wrap\"`.\n\n### 2. Add headless-UI regression test (HfGateHintLayoutTests.cs)\n\nNew file `tests/VisualRelay.Tests/HfGateHintLayoutTests.cs`:\n- `[Collection(\"Headless\")]` + `[AvaloniaFact]` (headless rendering).\n- **Arrange:** `TestRepository.Create()`, `WriteConfig(\"dotnet test\", [])`, `WriteTask(\"alpha\", ...)`. Create `DictionaryEnvironmentAccessor`, call `SettingsTestHelpers.SeedUserEnv(env, repo, \"\")` (no `HF_TOKEN`). Build `MainWindowViewModel` with `RootPath` and `EnvironmentAccessor`, `await LoadInitialAsync()`, assert `ShowHfGate == true`.\n- **Render:** `new MainWindow { DataContext = vm, Width = 1440, Height = 900 }`, `window.Show()`, `Dispatcher.UIThread.RunJobs()`.\n- **Resolve:** `QueuePanel` from visual descendants, then find the hint `TextBlock` by text prefix `\"or open Settings\"`.\n- **Assert:** `hintBlock.TranslatePoint(new Point(hintBlock.Bounds.Width, 0), queuePanel).Value.X ≤ 280.0` — fails before the fix (≈341 px), passes after (wraps within ~250 px). Additional guard: `hintBlock.Bounds.Width > 20` (not collapsed to zero).\n\n### 3. Verify\n- `./visual-relay check` passes.\n- Existing tests (`KeySetupPanelUiTests`, `ConfigInitEmptyStateUiTests`, `InitPanelButtonsLayoutTests`) remain green.\n- Headless re-screenshot of the no-keys `MainWindow` confirms the full hint is visible.",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/QueuePanel.axaml",
    "tests/VisualRelay.Tests/HfGateHintLayoutTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/HfGateHintLayoutTests.cs"],
  "rationale": "The HfGateHintLayoutTests.HfGateHint_IsNotClipped_AtFixed280PxPanelWidth test was verified with TDD: it fails with the original horizontal layout (right edge 342 px > 280 px panel, text clipped) and passes after the fix (hint stacked vertically with TextWrapping='Wrap', right edge stays within 280 px). The QueuePanel.axaml fix removes the inner horizontal StackPanel that placed the hint beside the button, making both vertical siblings in the outer StackPanel with TextWrapping='Wrap' on the hint so it uses the full ~250 px content width. All 16 related tests pass, including InitPanelButtonsLayoutTests, ConfigInitEmptyStateUiTests, KeySetupPanelUiTests, and HfGateBannerVisibilityTests."
}

## Stage 6 - Implement

{
  "summary": "Applied the HF-gate hint layout fix in QueuePanel.axaml (lines 215-223): removed the inner horizontal StackPanel that placed the hint TextBlock beside the button, making both direct vertical children of the outer StackPanel with TextWrapping=\"Wrap\" on the hint. The HfGateHintLayoutTests.HfGateHint_IsNotClipped_AtFixed280PxPanelWidth test passes, confirming the hint's right edge no longer exceeds the 280px panel width."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{
  "summary": "Fix confirmed. QueuePanel.axaml lines 215-223 already have the hint TextBlock stacked vertically below the button with TextWrapping=\"Wrap\" — no inner horizontal StackPanel. HfGateHintLayoutTests.cs exists and passes (1/1, 394 ms), asserting the hint's right edge ≤ 280 px at the fixed panel width."
}

## Stage 9 - Verify

{
  "summary": "Fixed HF-gate hint clipping in QueuePanel by moving the 'or open Settings ⚙ in the top bar' TextBlock from a cramped horizontal layout beside the button to its own line below it with TextWrapping=\"Wrap\". The hint no longer overflows the fixed 280 px panel width. A headless-UI regression test (HfGateHintLayoutTests) asserts the hint's right edge stays ≤ 280 px. All 2206 tests pass.",
  "commitMessages": [
    "fix: stack HF-gate hint below button to prevent horizontal clipping at 280 px",
    "fix(QueuePanel): move hint TextBlock to own row with Wrap to avoid overflow clip",
    "fix: prevent HF-gate banner hint from being cut off at fixed 280 px panel width",
    "fix: apply vertical-stack + TextWrapping fix to HF-gate hint (mirrors init-panel fix)"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

