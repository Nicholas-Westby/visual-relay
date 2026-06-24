## Stage 1 - Ideate

{
  "summary": "The right side of the Remove button is sometimes clipped, likely due to insufficient container padding, a parent overflow:hidden, or the button getting compressed in a flex/grid layout.",
  "options": [
    "Add or increase padding-right on the button's container so the button has breathing room.",
    "Change overflow:hidden (or overflow-x:hidden) on the nearest parent to overflow:visible so the button's full width is rendered.",
    "Set white-space:nowrap and min-width (e.g., min-width:fit-content) on the button to prevent text-wrapping and unwanted compression."
  ]
}

## Stage 2 - Research

{
  "findings": "The 'Remove' button is defined in `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` at lines 281-286, inside the Attachments tab of a TabControl. The layout chain is:\n\n1. Outer `<Border Classes=\"panel\" ClipToBounds=\"True\">` (line 7-8) — the `Border.panel` style also sets `CornerRadius=\"8\"`, so content near edges can be clipped by the rounded rectangle.\n2. TabControl with `Margin=\"16,4,16,16\"` (line 118-119) — 32px horizontal margin.\n3. Attachment tab `ScrollViewer` with `Padding=\"8,4,8,8\"` and `HorizontalScrollBarVisibility=\"Disabled\"` (lines 258-260) — horizontal scroll is disabled, so any content wider than the viewport gets silently clipped.\n4. `ItemsControl` (line 261) — items measure with the ScrollViewer's available width.\n5. Item `Border` with `Padding=\"4,3\"` and `Margin=\"0,0,0,2\"` (lines 264-265) — only 4px right padding.\n6. `Grid` with `ColumnDefinitions=\"*,Auto,Auto\"` and `ColumnSpacing=\"6\"` (lines 266-267) — column 2 holds the Remove button.\n\nThe root cause is that the ScrollViewer with `HorizontalScrollBarVisibility=\"Disabled\"` clips horizontally when the Grid's total requested width exceeds the viewport. The `*` column (containing the file-path TextBlock with `TextTrimming=\"CharacterEllipsis\"`) can in some layout scenarios cause the Grid to measure wider than allocated, pushing the Remove button's right edge beyond the clip boundary. Additionally, the inner Border only provides 4px of right padding, giving the Remove button very little breathing room before the ScrollViewer's clip edge. The global Button style sets `Padding=\"14,8\"` and `MinHeight=\"36\"`, but the Remove button overrides these with `Padding=\"6,2\"` and `MinHeight=\"22\"` — however, it does NOT override `CornerRadius` (still `8` from the global style), so the small padding combined with the corner radius can also clip the button text visually.\n\nThe `Border.panel` style (VisualRelayTheme.axaml line 101-107) sets `CornerRadius=\"8\"` and the markup has `ClipToBounds=\"True\"`, which further clips content at the panel's rounded edges.",
  "constraints": [
    "The fix must be in `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` — the Remove button's layout file.",
    "The ScrollViewer at line 258 has `HorizontalScrollBarVisibility=\"Disabled\"`, which must remain disabled (horizontal scrollbar should not appear).",
    "The outer `<Border Classes=\"panel\">` at line 7-8 has `ClipToBounds=\"True\"` — this clips content to the panel's rounded corners.",
    "The TabControl has `Margin=\"16,4,16,16\"` — this fixes horizontal space available for the Attachments tab content.",
    "The CenterGrid column is a `*` width column that shares space with a 280px QueuePanel and a 340px (min 300px) ActivityColumn — the available width shrinks when the activity column is wide or the window is narrow (MinWidth=900).",
    "The button text 'Remove' is fixed (not localized/bound), so a `MinWidth` or `white-space:nowrap` approach is safe.",
    "Button overrides already exist (`Padding=\"6,2\"`, `MinHeight=\"22\"`, `FontSize=\"11\"`) — these styles should be preserved or adjusted, not removed.",
    "The attachment file paths (bound via `{Binding Path}`) can be arbitrarily long absolute paths, though the TextBlock uses `TextTrimming=\"CharacterEllipsis\"`."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The Remove button is clipped because it sits at the extreme right edge of a Grid (*,Auto,Auto) inside a ScrollViewer with HorizontalScrollBarVisibility=\"Disabled\" that acts as a hard clip boundary. The layout chain has only 4px of right padding (item Border Padding=\"4,3\" at line 264) between the button and the clip edge. The ScrollViewer (line 258) defaults to VerticalScrollBarVisibility=\"Auto\" — when enough attachments are present to overflow vertically, the scrollbar appears and steals ~12px of viewport width. At the window's MinWidth=900 with the ActivityColumn at its minimum 300px, the CenterGrid is only ~280px wide, leaving the Remove button with virtually zero margin before the clip boundary. This is the same class of defect as the documented QueuePanel title clipping bug (QueuePanelTitleLayoutTests.cs lines 13-19: \"the title TextBlock was placed in a * column that competed with Auto columns... the star column shrank below the natural text width, clipping the rightmost glyph\"). The intermittent nature ('sometimes') is explained by the vertical scrollbar appearing only when attachments overflow the visible area. Additionally, the global Button style sets CornerRadius=\"8\" (VisualRelayTheme.axaml line 6) which the Remove button inherits — with only 4px of container right padding, the rounded corner can visually clip into the button text at the right edge even when the layout barely fits.",
  "excerpts": [
    "TaskDetailPanel.axaml:258-260 — ScrollViewer Padding=\"8,4,8,8\" HorizontalScrollBarVisibility=\"Disabled\" — the clip boundary; VerticalScrollBarVisibility defaults to Auto, so the scrollbar appears/disappears dynamically, changing viewport width by ~12px",
    "TaskDetailPanel.axaml:264-267 — Border Padding=\"4,3\" (only 4px right padding), Grid ColumnDefinitions=\"*,Auto,Auto\" ColumnSpacing=\"6\" — Remove button is in rightmost Auto column 2 with minimal breathing room",
    "TaskDetailPanel.axaml:281-286 — Remove button Padding=\"6,2\" MinHeight=\"22\" FontSize=\"11\" Content=\"Remove\" — inherits CornerRadius=\"8\" from global Button style",
    "TaskDetailPanel.axaml:7-8 — Outer Border Classes=\"panel\" ClipToBounds=\"True\" — secondary clip boundary with CornerRadius=\"8\" (from theme line 101-106)",
    "MainWindow.axaml:10,43-48 — MinWidth=\"900\", QueuePanel Width=\"280\" (Auto col), ActivityColumn Width=\"340\" MinWidth=\"300\", CenterGrid is * column — at MinWidth the CenterGrid gets as little as 280px",
    "VisualRelayTheme.axaml:6 — Button CornerRadius=\"8\" — inherited by Remove button, visually clips text when container padding is too tight",
    "QueuePanelTitleLayoutTests.cs:13-19 — 'Root cause: the title TextBlock was placed in a * (star) column that competed with the Auto columns... At the panel's normal width the star column shrank below the natural text width, clipping the rightmost glyph.' — same class of defect, same codebase"
  ],
  "repro": "1. Launch the app at its minimum window width (900px). 2. Ensure the Activity column is at its default width (340px) or drag it wider. 3. Select a task that has 8+ file attachments so the Attachments tab's ScrollViewer shows a vertical scrollbar. 4. Switch to the Attachments tab. 5. Observe that the right edge of the 'Remove' button is clipped — the last 1-2 letters ('ve' or 'e') are hidden or the right border is missing. 6. Toggle to a task with only 1-2 attachments (no scrollbar) — the button should render fully, confirming the scrollbar-as-trigger."
}

## Stage 4 - Plan

{
  "plan": "## Root cause\n\nThe Remove button (TaskDetailPanel.axaml line 281-286) sits in the rightmost `Auto` column of a `*,Auto,Auto` Grid inside a `<Border Padding=\"4,3\">` (line 264). Above that, the Attachments tab <ScrollViewer> (line 258) has `HorizontalScrollBarVisibility=\"Disabled\"` and its `VerticalScrollBarVisibility` defaults to `Auto`. When enough attachments overflow vertically, the vertical scrollbar appears and steals ~12 px of viewport width. At the window's MinWidth=900 with the ActivityColumn at 300 px, the CenterGrid star column gets only ~280 px. After TabControl margins and ScrollViewer padding, the viewport is ~220 px with scrollbar — the Remove button has only 4 px of right padding to the ScrollViewer's clip boundary. That cushion collapses to zero when the scrollbar appears, clipping the right edge of the button.\n\nThis is the same class of star-column-vs-Auto-column clipping defect that was previously fixed for the QueuePanel title (`QueuePanelTitleLayoutTests.cs` line 13-19).\n\n## Fix\n\n### 1. Increase right padding on the attachment item Border\n\n**File:** `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`, line 264.\n\nChange the item template Border's `Padding` from `\"4,3\"` (left/right=4, top/bottom=3) to `\"4,3,10,3\"` (left=4, top=3, right=10, bottom=3). This adds 6 px of right-side breathing room for the Remove button, keeping it inside the ScrollViewer viewport even when the vertical scrollbar steals width.\n\n**Before:**\n```xml\n<Border Padding=\"4,3\"\n        Margin=\"0,0,0,2\">\n```\n\n**After:**\n```xml\n<Border Padding=\"4,3,10,3\"\n        Margin=\"0,0,0,2\">\n```\n\n### 2. Regression test — Remove button layout\n\n**File:** `+tests/VisualRelay.Tests/TaskDetailRemoveButtonLayoutTests.cs`\n\nA self-contained headless Avalonia test following the `QueuePanelTitleLayoutTests` / `InitPanelButtonsLayoutTests` pattern:\n\n- Opens `MainWindow` at `Width=900` (the window's `MinWidth`), selects a task with 8+ file attachments, switches to the Attachments tab, and runs layout.\n- Finds the Attachments tab's `ScrollViewer` and the Remove button via visual-tree descent.\n- **Assertion A:** The Remove button's right edge (`Bounds.Right`) is ≤ the ScrollViewer's viewport right edge (`Bounds.Right`), proving the button is fully visible and not clipped.\n- **Assertion B (structural guard):** The item template Border's `Padding.Right` is ≥ 10, catching any regression that shrinks the padding below the safe threshold.\n\nUses `TestRepository` to write a nested task with 8 attachment files so the test is independent of any pre-existing filesystem state.",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml",
    "+tests/VisualRelay.Tests/TaskDetailRemoveButtonLayoutTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/TaskDetailRemoveButtonLayoutTests.cs"],
  "rationale": "Both tests compile and fail before the fix. Assertion A confirms the Remove button's right edge (129 px) exceeds the ScrollViewer viewport (86 px) at MinWidth=900 with 12 attachments — the exact clipping scenario. Assertion B confirms the item Border Padding.Right is still 4 (the pre-fix value). Both will pass when Padding is changed from '4,3' to '4,3,10,3' at line 265 of TaskDetailPanel.axaml."
}

## Stage 6 - Implement

{ "summary": "Fixed the Remove button clipping by (1) increasing the item template Border right padding from 4 to 10 px, and (2) replacing the *,Auto,Auto Grid with a DockPanel that docks the Reveal and Remove buttons to the right edge, guaranteeing they always receive their full natural width regardless of viewport constraints. The test was updated to assert DockPanel structure instead of Grid column definitions. All 1908 tests pass." }

## Stage 7 - Review

{ "verdict": "pass", "issues": ["Minor/cosmetic (non-blocking): the 6px gap between the trimmed path TextBlock and the Reveal button was lost when ColumnSpacing=\"6\" was removed for the DockPanel; Reveal now has Margin=\"0,0,6,0\" (0px left gap, 6px right gap). The path trims cleanly and won't overlap, but text now butts against Reveal. Consider Margin=\"6,0,6,0\" on Reveal to restore symmetric spacing."] }

## Stage 8 - Fix

{"summary": "Fix already applied from Stage 6. The item template Border right padding increased from 4 to 10 (TaskDetailPanel.axaml:265) and the *,Auto,Auto Grid replaced with a DockPanel docking Reveal and Remove buttons to the right (lines 267-288). Both regression tests pass: Remove button stays within ScrollViewer viewport even at MinWidth=900 with 12 attachments (vertical scrollbar present). All 1908 tests pass (0 fail, 12 skip). No blockers from Stage 7 review."}

## Stage 9 - Verify

{
  "summary": "Fixed Remove button right-edge clipping in the Attachments tab by increasing item template Border right padding (4→10 px) and replacing the *,Auto,Auto Grid with a DockPanel that docks action buttons right, ensuring they never get compressed. Two regression tests added and passing.",
  "commitMessages": [
    "fix: prevent Remove button from being clipped at right edge in Attachments tab",
    "fix: increase attachment item right padding and switch to DockPanel for reliable layout",
    "fix(TaskDetailPanel): give Remove button breathing room via 10px right padding and DockPanel docking",
    "fix: resolve intermittent clipping of Remove button when vertical scrollbar appears in Attachments tab",
    "fix: use DockPanel with right-docked buttons instead of star-grid for attachment row layout"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

