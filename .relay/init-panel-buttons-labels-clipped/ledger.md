## Stage 1 - Ideate

{
  "summary": "The QueuePanel's \"Initialize this project\" empty-state card clips its two action-button labels (\"Create config\" → \"Create conf\", \"Find it for me\" → \"Find it for m\") because the buttons share a `ColumnDefinitions=\"*,*\"` grid inside a fixed 280 px panel. Each column is ~102 px, too narrow for the text plus 12,6 padding. The fix is to replace the two-column Grid with a single-column layout so each button gets the full ~212 px content width, which is robust to longer labels and requires no behaviour changes.",
  "options": [
    "Stack buttons vertically — replace `*,*` Grid with a single-column layout (StackPanel or single-column Grid), full-width buttons, preserves x:Name=\"CreateConfigButton\", Classes=\"primary\", and tooltip. Solves root cause permanently.",
    "Keep side-by-side with Auto-sized columns and enable text wrapping on buttons — changes `*,*` to `Auto,Auto` and sets TextWrapping=\"Wrap\". Fragile; multi-line buttons at ~102 px look odd and still risk clipping.",
    "Reduce padding/font size within the existing `*,*` grid — e.g. Padding=\"6,4\" or smaller font. Fragile, breaks on longer labels, violates robustness goal."
  ]
}

## Stage 2 - Research

{ "findings": "The QueuePanel is 280 px fixed. The empty-state card (Border InitEmptyState) has Margin=16/Padding=16 leaving ~212 px inner width. The two action buttons live in a `Grid ColumnDefinitions=\"*,*\" ColumnSpacing=\"8\"` — each column ~102 px. With button Padding=12,6 the text area is ~80 px, too narrow for \"Create config\" or \"Find it for me\", so the outer ClipToBounds clips the overflow. The global button style sets Padding=14,8/MinHeight=36. The init card is a StackPanel child — the button grid and the \"Set up empty project\" button are siblings. The existing test resolves CreateConfigButton by x:Name. No existing test asserts button widths. The 280 px panel width is intentional and must not change.", "constraints": ["QueuePanel width (280 px) is fixed — do not widen.", "Preserve x:Name=\"CreateConfigButton\" — existing UI test finds it by name.", "Preserve Classes=\"primary\" on Create config button.", "Preserve ToolTip on \"Find it for me\" button.", "No behaviour changes to either command (CreateConfigCommand, FindTestCommandCommand).", "Existing ConfigInitEmptyStateUiTests must remain green after layout change.", "No font/padding/text-wrapping workarounds — stack vertically instead.", "Buttons must get full ~212 px width, robust to longer/locale labels."] }

## Stage 3 - Diagnose

{
  "evidence": "The two action buttons in InitEmptyState share a Grid ColumnDefinitions=\"*,*\" (QueuePanel.axaml:154). At the fixed 280 px panel width (MainWindow.axaml:52), each column is ~103 px. Button Padding=\"12,6\" leaves ~79 px for labels — \"Create config\" needs ~110+ px. The outer Border ClipToBounds=\"True\" (QueuePanel.axaml:8) clips the overflow, producing the observed \"Create conf\" / \"Find it for m\" truncation. Fix: stack buttons vertically for full ~212 px width.",
  "excerpts": [
    "MainWindow.axaml:52 — <controls:QueuePanel Width=\"280\"",
    "QueuePanel.axaml:7-8 — <Border Classes=\"panel\" ClipToBounds=\"True\">",
    "QueuePanel.axaml:136-144 — <Border x:Name=\"InitEmptyState\" Margin=\"16\" Padding=\"16\"",
    "QueuePanel.axaml:154 — <Grid ColumnDefinitions=\"*,*\" ColumnSpacing=\"8\">",
    "QueuePanel.axaml:155-159 — <Button x:Name=\"CreateConfigButton\" ... Padding=\"12,6\" ... Content=\"Create config\"/>",
    "QueuePanel.axaml:162-167 — <Button Grid.Column=\"1\" ... Padding=\"12,6\" ... Content=\"Find it for me\"",
    "VisualRelayTheme.axaml:3-10 — Global Button style: Padding=\"14,8\", MinHeight=\"36\"",
    "ConfigInitEmptyStateUiTests.cs:62 — var button = queuePanel.FindControl<Button>(\"CreateConfigButton\");"
  ],
  "repro": "Set NeedsInitialization=true, render QueuePanel at 280 px width. Measure CreateConfigButton arranged Bounds.Width (~102 px) vs its content desired width (~110+ px). The button width is less than its label's desired width, confirming the label is constrained by the star-sized column and clipped by ClipToBounds."
}

## Stage 4 - Plan

{
  "plan": "## Plan: Fix init-panel action-button labels clipped\n\n### Root cause\nIn `QueuePanel.axaml`, the `InitEmptyState` StackPanel child at line 154 is a `<Grid ColumnDefinitions=\"*,*\">` that splits the ~212 px content width into two ~102 px columns. Each button's `Padding=\"12,6\"` leaves ~79 px for text labels that need ~110+ px. The outer `<Border ClipToBounds=\"True\">` (line 8) clips the overflow, truncating \"Create config\" → \"Create conf\" and \"Find it for me\" → \"Find it for m\".\n\n### Fix (one change in QueuePanel.axaml)\nReplace the `*,*` Grid (lines 154–168) with a **vertical StackPanel**, `Spacing=\"8\"`, each button `HorizontalAlignment=\"Stretch\"`. This gives each button the full ~212 px inner width — ample for labels and robust to longer/localized strings. The card's parent StackPanel already uses `Spacing=\"10\"`, so the 8 px spacing between the two buttons matches the original grid's `ColumnSpacing` and keeps the rhythm. Vertical room is ample; the card is in the QueuePanel's `*` grid row. All existing bindings, names, classes, and tooltips are preserved exactly.\n\n### TDD test (new file)\nFile: `tests/VisualRelay.Tests/InitPanelButtonsLayoutTests.cs`\n- One `[AvaloniaFact]` method: creates a config-less repo (like `ConfigInitEmptyStateUiTests`), shows MainWindow, resolves the `QueuePanel`, finds `CreateConfigButton` by `x:Name` and the \"Find it for me\" button by content text.\n- Asserts the two buttons are vertically stacked: `Math.Abs(a.Bounds.X - b.Bounds.X) < 1.0` (same column) AND `a.Bounds.Y < b.Bounds.Y` (CreateConfigButton above Find-it-for-me).\n- Asserts each button's arranged `Bounds.Width` is ≥ the label's `DesiredSize.Width` minus 1 px rounding, confirming no text clipping at the fixed 280 px panel width.\n- This test **fails** on today's `*,*` side-by-side layout (buttons have different X, each ~102 px wide but labels need more) and **passes** once stacked vertically.\n\n### Existing test preservation\n`ConfigInitEmptyStateUiTests` resolves `CreateConfigButton` by `x:Name` and clicks it with `TranslatePoint`-based coordinates — these are name-scope and geometry calculations that work identically whether the button is in a Grid or a StackPanel. No changes needed to keep it green.\n\n### Out of scope\n- \"Set up empty project\" dead-click (tracked separately).\n- Footer status line, task ListBox, HasConfigDiagnostic border.\n- Panel width change (280 px is fixed).",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/QueuePanel.axaml",
    "+tests/VisualRelay.Tests/InitPanelButtonsLayoutTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/InitPanelButtonsLayoutTests.cs"],
  "rationale": "Added InitPanelButtonsLayoutTests with one [AvaloniaFact] that: (1) asserts the two action buttons are vertically stacked (same X within 1 px, CreateConfigButton above FindItButton) — this fails on today's *,* Grid where buttons sit side-by-side (X=0.0 vs X=110.0); (2) asserts each button's arranged Bounds.Width ≥ its TextBlock label's unconstrained desired width (obtained via Measure(Size.Infinity)) — confirming no text clipping. Both assertions fail on the current layout and will pass once buttons are stacked vertically at full ~212 px width. Existing ConfigInitEmptyStateUiTests remains green."
}

## Stage 6 - Implement

{
  "summary": "Fixed the clipped button labels in the \"Initialize this project\" empty-state card. Root cause: the two action buttons shared a `Grid ColumnDefinitions=\"*,*\"` that split the ~212 px inner content width into two ~102 px columns — too narrow for labels plus padding, so the outer `ClipToBounds=\"True\"` Border clipped them. Fix: replaced the `*,*` Grid with a vertical `<StackPanel Spacing=\"8\">` (QueuePanel.axaml:154), giving each button the full ~212 px width via `HorizontalAlignment=\"Stretch\"`. All bindings, `x:Name=\"CreateConfigButton\"`, `Classes=\"primary\"`, and tooltip preserved. The new `InitPanelButtonsLayoutTests` (asserts vertical stacking + sufficient button width for labels) and the existing `ConfigInitEmptyStateUiTests` both pass (2 passed, 0 failed)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified the fix is in place: the two action buttons in InitEmptyState are vertically stacked in a StackPanel (QueuePanel.axaml:154-166), each stretched to full ~212 px width. Both InitPanelButtonsLayoutTests (vertical stacking + width sufficiency) and ConfigInitEmptyStateUiTests pass (2 passed, 0 failed). No further changes needed." }

## Stage 9 - Verify

{
  "summary": "Fixed clipped action-button labels in the \"Initialize this project\" empty-state card. Root cause: the two buttons shared a `Grid ColumnDefinitions=\"*,*\"` that split the ~212 px inner content width into ~102 px columns — too narrow for \"Create config\" and \"Find it for me\" plus 12,6 padding, so the outer `ClipToBounds=\"True\"` Border truncated them. Fix: replaced the `*,*` Grid with a vertical `<StackPanel Spacing=\"8\">` in `QueuePanel.axaml` (line 154), giving each button the full ~212 px width via `HorizontalAlignment=\"Stretch\"`. All bindings, `x:Name=\"CreateConfigButton\"`, `Classes=\"primary\"`, and the \"Find it for me\" tooltip are preserved. New test file `InitPanelButtonsLayoutTests.cs` asserts vertical stacking (same X, increasing Y) and sufficient button width for each label. Both the new test and the existing `ConfigInitEmptyStateUiTests` pass (2 passed, 0 failed).",
  "commitMessages": [
    "fix: stack empty-state action buttons vertically to prevent label truncation",
    "fix(QueuePanel): replace *,* two-column grid with vertical StackPanel in init card",
    "test: add InitPanelButtonsLayoutTests asserting full-width vertically stacked buttons",
    "fix: give empty-state buttons full 212px width by stacking instead of side-by-side",
    "fix(ui): unclip 'Create config' and 'Find it for me' in init empty-state card"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

