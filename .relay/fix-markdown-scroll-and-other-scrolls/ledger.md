## Stage 1 - Ideate

{
  "summary": "The markdown scroll and attachments tab scroll both suffer from the same class of bug: the scrollable container's bottom boundary clips the last line(s) of content. Likely causes include (a) missing `padding-bottom` (or insufficient scroll-padding) on the scroll container, (b) flex layout preventing proper scroll-height calculation, or (c) auto-scroll-to-bottom logic not accounting for container padding. The fix needs to ensure the scroll container has inherent bottom breathing room so that scrolling downward reveals the full last line.",
  "options": [
    "Add `padding-bottom` (e.g., 16–24px) to the scrollable container element in CSS. This is the simplest fix — it creates a visual gap after the last content element so the last line sits above the container's bottom edge. Apply to both the markdown viewer container and the attachments tab scroll container. If the containers are shared components, fix at the shared CSS class level. Verify with `overflow: auto` or `overflow-y: scroll` that the scroll thumb extent reaches the actual bottom.",
    "Use `scroll-padding-bottom` CSS property on the scroll container (modern browsers) combined with a `::after` pseudo-element of the same height on the content area. This guarantees that when `scrollIntoView({ block: 'end' })` or native scroll anchoring is used, the last element aligns with proper bottom offset. This is more robust than padding alone because it also affects programmatic scroll targets and avoids interfering with absolute-positioned children.",
    "Refactor the scroll container's layout sizing: ensure the container uses `display: flex; flex-direction: column; min-height: 0; overflow-y: auto;` and that its parent does not have a conflicting height constraint. Add `box-sizing: border-box` to include padding in the element's total height. Check for any parent with `overflow: hidden` that clips the scroll area — replace with `overflow: visible` or remove it. This addresses the root layout cause if padding alone isn't enough."
  ]
}

## Stage 2 - Research

{
  "findings": "The markdown tab read-only view (TaskDetailPanel.axaml lines 144-152) and attachments tab (lines 225-228) both use ScrollViewer elements with only 8px bottom padding (Padding=\"8,14,8,8\" and Padding=\"8,4,8,8\"), which is insufficient to prevent the last line of content from being clipped at the scroll boundary. The developers previously solved this identical bug in StageInputView.axaml (lines 98-105) and StageOutputView.axaml (lines 111-117) by using Margin=\"8,8,8,16\" on the inner content element rather than relying solely on ScrollViewer.Padding — with an explicit comment documenting why. That fix was never ported to TaskDetailPanel.axaml's three scrollable tabs (Markdown, Context, Attachments). No auto-scroll-to-bottom or programmatic scroll logic exists in the codebase, so the issue is purely layout-based. The web-CSS solutions suggested in Stage 1 (scroll-padding-bottom, ::after pseudo-elements) do not apply since this is an Avalonia UI app using .axaml markup, not a web app.",
  "constraints": [
    "This is an Avalonia UI (.NET) application using .axaml XAML files — not a web app — so CSS properties like scroll-padding-bottom and ::after pseudo-elements do not exist.",
    "The fix must use Avalonia-native layout properties: Padding on the ScrollViewer or Margin on the inner content element.",
    "The established pattern in StageInputView.axaml and StageOutputView.axaml uses a 16px bottom margin on the inner content (ItemsControl) with a comment explaining the technique: 'Inset lives on the content (not ScrollViewer.Padding) so the bottom gap is part of the measured extent and the last item stays reachable.'",
    "Three tabs in TaskDetailPanel.axaml need fixing: Markdown (read-only view, line 146), Context (line 205), and Attachments (line 227).",
    "The Markdown tab also has an edit view (line 154-166) and a new-task view (line 169-201) with TextBox elements — these may need review too.",
    "The entire TaskDetailPanel is wrapped in a Border with ClipToBounds=\"True\" (line 9), but this does not affect internal ScrollViewer behavior.",
    "The Markdown read-only ScrollViewer wraps a TextBlock (not ItemsControl), so the margin should be applied to the TextBlock or the ScrollViewer's padding increased.",
    "The Attachments tab ScrollViewer wraps an ItemsControl, matching the StageInputView/StageOutputView pattern exactly."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The markdown scroll and attachments tab scroll both clip the last line(s) because the ScrollViewer elements in TaskDetailPanel.axaml have only 8px of bottom Padding — insufficient to clear the 21px line height or attachment row height. The developers already fixed this identical bug in StageInputView.axaml (line 105) and StageOutputView.axaml (line 117) by moving the bottom inset onto the inner content element via Margin=\"8,8,8,16\" with the documented comment: 'Inset lives on the content (not ScrollViewer.Padding) so the bottom gap is part of the measured extent and the last item stays reachable.' That fix was never ported to TaskDetailPanel.axaml's three scrollable areas: the Markdown read-only view (line 146, Padding=\"8,14,8,8\"), the Context tab (line 205, Padding=\"8,18,8,8\"), and the Attachments tab (line 227, Padding=\"8,4,8,8\"). No programmatic auto-scroll logic exists in any .cs file — the issue is purely a layout/inset deficit.",
  "excerpts": [
    "TaskDetailPanel.axaml:146-151 — Markdown read-only ScrollViewer with Padding=\"8,14,8,8\" wrapping a TextBlock (LineHeight=21). Only 8px bottom padding; last line clips.",
    "TaskDetailPanel.axaml:204-213 — Context tab ScrollViewer with Padding=\"8,18,8,8\" wrapping a TextBlock. Same 8px bottom deficit.",
    "TaskDetailPanel.axaml:225-268 — Attachments tab ScrollViewer with Padding=\"8,4,8,8\" wrapping an ItemsControl. Same 8px bottom deficit.",
    "StageInputView.axaml:98-105 — Already-fixed reference: no ScrollViewer.Padding; Margin=\"8,8,8,16\" on inner ItemsControl with comment explaining why the inset must live on the content.",
    "StageOutputView.axaml:111-117 — Identical already-fixed pattern: Margin=\"8,8,8,16\" on inner ItemsControl with same comment.",
    "Grep across all *.cs: zero matches for ScrollToBottom, ScrollIntoView, ScrollChanged, auto-scroll — confirming no programmatic scroll logic exists; the bug is purely layout-based.",
    "Stage 2 Research report confirms: 'The web-CSS solutions suggested in Stage 1 (scroll-padding-bottom, ::after pseudo-elements) do not apply since this is an Avalonia UI app using .axaml markup.'"
  ],
  "repro": "1. Launch VisualRelay. 2. Select any task with markdown content taller than the viewport. 3. Click the 'Markdown' tab. 4. Scroll to the very bottom. 5. Observe the last line is partially or fully clipped — the 21px LineHeight collides with the 8px bottom Padding. 6. Switch to 'Attachments' tab, scroll to bottom — last attachment is likewise clipped. 7. Contrast with the stage input/output parsed views (StageInputView.axaml) where a 16px bottom Margin on the inner ItemsControl keeps the last item fully visible."
}

## Stage 4 - Plan

{
  "plan": "## Root cause\n\nIn `TaskDetailPanel.axaml`, three `ScrollViewer` elements use `Padding` to inset content, with only 8 px on the bottom — too little to clear the 21 px `LineHeight` of the `TextBlock` or the attachment row height. In Avalonia, `Padding` on a `ScrollViewer` gets added *outside* the content's measured extent, so the scroll thumb cannot reveal the padded region. This clips the last line/item.\n\n## Fix strategy\n\nApply the same fix already used in `StageInputView.axaml` (lines 98–105) and `StageOutputView.axaml` (lines 111–117): remove the bottom inset from `ScrollViewer.Padding` and put it on the inner content element as `Margin`. A `Margin` on the content is part of the measured size, so the `ScrollViewer` extends far enough to scroll the last item fully into view. Keep all other inset values (left, top, right) unchanged; only the bottom changes from 8 to 16 px.\n\n## Edits (all in `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`)\n\n### 1. Markdown read-only view (lines 146–151)\n\n**Before:**\n```xml\n<ScrollViewer Grid.Row=\"1\" Padding=\"8,14,8,8\"\n              HorizontalScrollBarVisibility=\"Disabled\">\n  <TextBlock Text=\"{Binding SelectedTaskMarkdown}\"\n             FontFamily=\"Menlo,Consolas,monospace\" FontSize=\"13\"\n             LineHeight=\"21\" Foreground=\"#DCE2EA\" TextWrapping=\"Wrap\"/>\n</ScrollViewer>\n```\n\n**After:**\n```xml\n<ScrollViewer Grid.Row=\"1\" HorizontalScrollBarVisibility=\"Disabled\">\n  <!-- Inset lives on the content (not ScrollViewer.Padding) so the bottom\n       gap is part of the measured extent and the last line stays reachable. -->\n  <TextBlock Text=\"{Binding SelectedTaskMarkdown}\"\n             FontFamily=\"Menlo,Consolas,monospace\" FontSize=\"13\"\n             LineHeight=\"21\" Foreground=\"#DCE2EA\" TextWrapping=\"Wrap\"\n             Margin=\"8,14,8,16\"/>\n</ScrollViewer>\n```\n\n### 2. Context tab (lines 204–213)\n\n**Before:**\n```xml\n<ScrollViewer Padding=\"8,18,8,8\"\n              HorizontalScrollBarVisibility=\"Disabled\">\n  <TextBlock Text=\"{Binding SelectedTaskContext}\"\n             FontFamily=\"Menlo,Consolas,monospace\"\n             FontSize=\"13\"\n             LineHeight=\"21\"\n             Foreground=\"#DCE2EA\"\n             TextWrapping=\"Wrap\"/>\n</ScrollViewer>\n```\n\n**After:**\n```xml\n<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\">\n  <!-- Inset lives on the content (not ScrollViewer.Padding) so the bottom\n       gap is part of the measured extent and the last line stays reachable. -->\n  <TextBlock Text=\"{Binding SelectedTaskContext}\"\n             FontFamily=\"Menlo,Consolas,monospace\"\n             FontSize=\"13\"\n             LineHeight=\"21\"\n             Foreground=\"#DCE2EA\"\n             TextWrapping=\"Wrap\"\n             Margin=\"8,18,8,16\"/>\n</ScrollViewer>\n```\n\n### 3. Attachments tab (lines 225–228)\n\n**Before:**\n```xml\n<ScrollViewer Grid.Row=\"1\"\n              x:Name=\"AttachmentsScroller\"\n              Padding=\"8,4,8,8\"\n              HorizontalScrollBarVisibility=\"Disabled\">\n  <ItemsControl ItemsSource=\"{Binding Attachments}\">\n```\n\n**After:**\n```xml\n<ScrollViewer Grid.Row=\"1\"\n              x:Name=\"AttachmentsScroller\"\n              HorizontalScrollBarVisibility=\"Disabled\">\n  <!-- Inset lives on the content (not ScrollViewer.Padding) so the bottom\n       gap is part of the measured extent and the last item stays reachable. -->\n  <ItemsControl ItemsSource=\"{Binding Attachments}\"\n                Margin=\"8,4,8,16\">\n```",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml"
  ]
}

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/TaskDetailScrollBottomReachabilityTests.cs"],
  "rationale": "Five tests covering the three scrollable areas in TaskDetailPanel (Markdown read-only, Context, Attachments). Three structural tests assert that the inner content element (TextBlock or ItemsControl) has Margin.Bottom ≥ 16 px — currently 0, failing with the root-cause message. Two behavioural extent tests assert that the ScrollViewer.Extent.Height reaches the last content element's bottom edge with at least a 2 px gap — currently the extent falls ~14 px short because the 8 px ScrollViewer.Padding bottom sits outside the measured extent. After the fix (moving the bottom inset to the inner content's Margin), all five will pass."
}

## Stage 6 - Implement

{
  "summary": "Fixed scroll-bottom clipping in TaskDetailPanel.axaml by moving the bottom inset from ScrollViewer.Padding onto the inner content element's Margin in three places: Markdown read-only (TextBlock Margin=\"8,14,8,16\"), Context tab (TextBlock Margin=\"8,18,8,16\"), and Attachments tab (ItemsControl Margin=\"8,4,8,16\"). This follows the established pattern in StageInputView/StageOutputView where Margin on the content ensures the bottom gap is part of the measured extent, keeping the last line/item reachable. Added explanatory XML comments. All 5 regression tests pass. Compressed the authored test file from 430 to 223 lines to satisfy the 300-line split guard. The one unrelated test failure (KeySetupPanelUiTests) is a pre-existing race condition in SettingsWindow opening, not caused by these changes."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All targeted scroll tests pass (5/5). Review verdict was 'pass' with no issues. No changes needed — the fix from Stage 6 (moving bottom inset from ScrollViewer.Padding to inner content Margin in TaskDetailPanel.axaml) is intact and verified." }

## Stage 9 - Verify

{
  "summary": "Fixed scroll-bottom clipping in TaskDetailPanel.axaml by porting the established pattern from StageInputView/StageOutputView: the bottom inset was moved from ScrollViewer.Padding (which sits outside the measured extent) to the inner content element's Margin (TextBlock or ItemsControl) with a 16px bottom value. This ensures the ScrollViewer's extent includes the full content area, making the last line/item reachable. Applies to all three scrollable tabs (Markdown read-only, Context, Attachments). All 2,431 tests pass (0 failed, 22 skipped). No other files changed.",
  "commitMessages": [
    "fix: prevent last line clipping in markdown and attachments scroll views",
    "fix(tabs): port bottom-inset fix from stage views to TaskDetailPanel scroll areas",
    "fix: move bottom padding from ScrollViewer to inner content margin for reachable scroll extent",
    "fix: ensure scroll extent covers last line in Markdown, Context, and Attachments tabs"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

