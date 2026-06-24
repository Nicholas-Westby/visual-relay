## Stage 1 - Ideate

{
  "summary": "Three cosmetic fixes: (1) add VerticalAlignment=\"Center\" to ACTIVITY panelTitle in ActivityColumn.axaml, (2) change Reveal button Margin from \"0,0,6,0\" to \"6,0,6,0\" in TaskDetailPanel.axaml's attachment template, (3) optionally DRY grip-dot pointerover styles and update test assertion messages. Options differ on scope (skip DRY → lower risk, include DRY → cleaner code, alternate margin placement).",
  "options": [
    "Option A — Minimal targeted fixes: items 1, 2, and 3b only. Single commit. Lowest risk, skips the grip-dot refactor to guarantee no test interference. Recommended if speed/safety is priority.",
    "Option B — Full polish with DRY refactor: all items including 3a. Two commits (fix + refactor). Collapses three identical pointerover setters into one Classes-based Style block. Leaves codebase cleanest but needs pre-check that tests don't reference GripDot1/2/3 by name.",
    "Option C — Alternative spacing approach: items 1, 3b, and cautiously 3a. For item 2, add margin to the file-path TextBlock (DockPanel fill child) instead of widening the button margin. Keeps button margins unchanged; equally valid spacing fix."
  ]
}

## Stage 2 - Research

{
  "findings": "Four fix locations identified: (1) ActivityColumn.axaml line 41-43 — panelTitle TextBlock missing VerticalAlignment, fix by adding VerticalAlignment=\"Center\" to match QueuePanel pattern. (2) TaskDetailPanel.axaml line 279 — Reveal button Margin=\"0,0,6,0\" has no left gap, fix by changing to Margin=\"6,0,6,0\" to separate path text from button. (3a) MainWindow.axaml lines 145-153 — three identical GripDot1/2/3 pointerover styles can be safely collapsed into one Classes-based selector; tests only find dots by geometry (not by Name), so no test interference. (3b) TaskDetailRemoveButtonLayoutTests.cs line 124 — assertion message still says 'Border (line 265)' which is stale after Grid→DockPanel change; update wording to reference DockPanel template. All target files are ≤300 lines. No code-behind changes needed (both .axaml.cs are trivial).",
  "constraints": [
    "VR is general-purpose; no project-specific assumptions",
    "All target files are ≤300 lines",
    "./visual-relay check must remain green after changes",
    "Commits must use Conventional Commit subjects",
    "Cosmetic only — do not change collapse/resize behavior, DockPanel structure, or splitter appearance/hit area",
    "Item 3a (DRY grip dots) optional — only do if it doesn't fight tests that count named borders (it doesn't: Splitter_Grip_IsDotPattern_NotSolidBlock finds dots by geometry, not Name)",
    "Item 3b: update test assertion message text only — do not weaken assertions"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Four cosmetic issues confirmed across 4 files. (1) ActivityColumn.axaml:41-43 — panelTitle TextBlock missing VerticalAlignment=\"Center\" (QueuePanel:15-18 has it, causing visible misalignment with taller Reveal button in same Auto-height row). (2) TaskDetailPanel.axaml:279 — Reveal button Margin=\"0,0,6,0\" has zero left gap; file-path TextBlock (DockPanel fill child at line 281) butts directly against button. (3a) MainWindow.axaml:145-153 — three identical GripDot pointerover styles; test at ActivitySplitterAffordanceTests.cs:138-141 finds dots by geometry (Bounds.Width/Height), not by Name, so DRY via shared Classes selector is safe. (3b) TaskDetailRemoveButtonLayoutTests.cs:17 — class doc still says \"*,Auto,Auto Grid\" from before Grid→DockPanel refactor; assertion messages need updated wording.",
  "excerpts": [
    "ActivityColumn.axaml:41-43 <TextBlock Grid.Column=\"2\" Text=\"ACTIVITY\" Classes=\"panelTitle\"/> — no VerticalAlignment (cf QueuePanel:15-18 VerticalAlignment=\"Center\")",
    "TaskDetailPanel.axaml:274-280 Reveal button Margin=\"0,0,6,0\" — left margin is 0, no gap from path TextBlock (DockPanel fill child at line 281)",
    "MainWindow.axaml:145-153 three identical Style Selector=\"GridSplitter:pointerover /template/ Border#GripDot1/2/3\" — all set Background=\"#5B9CFF\"",
    "ActivitySplitterAffordanceTests.cs:138-141 finds dots by Bounds 3-6px range, not by Name — GripDot names are untested",
    "TaskDetailRemoveButtonLayoutTests.cs:17 \"*,Auto,Auto Grid inside a Border\" — stale after Grid→DockPanel change at TaskDetailPanel.axaml:267"
  ],
  "repro": "n/a — cosmetic issues visible in static code review; no runtime error to reproduce"
}

## Stage 4 - Plan

{
  "plan": "Four cosmetic fixes across 4 files:\n\n1. **ActivityColumn.axaml:41-43** — Add `VerticalAlignment=\"Center\"` to the ACTIVITY `panelTitle` TextBlock so it aligns vertically with the Reveal button in the same Auto-height row (matches QueuePanel pattern at QueuePanel.axaml:15-18).\n\n2. **TaskDetailPanel.axaml:279** — Change Reveal button `Margin` from `\"0,0,6,0\"` to `\"6,0,6,0\"` to restore the left-side gap between the file-path TextBlock (DockPanel fill child, line 281) and the button. The ellipsis prevents visual overlap; this is spacing only.\n\n3a. **MainWindow.axaml:122-136** — Add `Classes=\"gripDot\"` to each GripDot1/2/3 Border. **MainWindow.axaml:145-153** — Replace three identical `GridSplitter:pointerover /template/ Border#GripDot1/2/3` selectors (each setting `Background=\"#5B9CFF\"`) with a single `GridSplitter:pointerover /template/ Border.gripDot` selector. Visual result is identical. Confirmed safe: `ActivitySplitterAffordanceTests.Splitter_Grip_IsDotPattern_NotSolidBlock` finds dots by `Bounds.Width`/`Bounds.Height` range (3-6px), not by Name — no test interference.\n\n3b. **TaskDetailRemoveButtonLayoutTests.cs:17** — Update class doc: `\"*,Auto,Auto Grid inside a Border\"` → `\"DockPanel inside a Border\"` (stale after Grid→DockPanel refactor at TaskDetailPanel.axaml:267). **TaskDetailRemoveButtonLayoutTests.cs:123-124** — Update assertion message: `\"item template Border (line 265)\"` → `\"item template Border (DockPanel, line 265)\"`. Message text only; assertions unchanged.\n\nAll four fixes are cosmetic only — no behavioral changes to collapse/resize, DockPanel structure, or splitter appearance/hit area.",
  "manifest": [
    "src/VisualRelay.App/Views/Controls/ActivityColumn.axaml",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml",
    "src/VisualRelay.App/Views/MainWindow.axaml",
    "tests/VisualRelay.Tests/TaskDetailRemoveButtonLayoutTests.cs"
  ]
}

> **Manifest merge (stage 5)**: added 2 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ActivityColumnTitleLayoutTests.cs",
    "tests/VisualRelay.Tests/TaskDetailAttachmentRevealButtonLayoutTests.cs"
  ],
  "rationale": "Three tests covering the two cosmetic fixes:\n\n1. ActivityColumnTitleLayoutTests.ActivityTitle_HasVerticalAlignmentCenter — structural test asserting the ACTIVITY panelTitle TextBlock has VerticalAlignment=\"Center\". Fails now: actual is Stretch (default in Avalonia for TextBlock without explicit VerticalAlignment). Matches QueuePanel.axaml:18 pattern.\n\n2. TaskDetailAttachmentRevealButtonLayoutTests.AttachmentRevealButton_MarginLeft_IsSixOrMore — structural test asserting the Reveal button's Margin.Left ≥ 6. Fails now: actual is 0 (Margin=\"0,0,6,0\").\n\n3. TaskDetailAttachmentRevealButtonLayoutTests.AttachmentFilePath_HasGapBeforeRevealButton — visual guard measuring the rendered horizontal gap between the file-path TextBlock right edge and the Reveal button left edge. Fails now: gap is 0.0px (text butts directly against the button).\n\nAll three tests compile, build, and fail against the current codebase as verified with `dotnet test --filter \"FullyQualifiedName~ActivityColumnTitleLayoutTests|FullyQualifiedName~TaskDetailAttachmentRevealButtonLayoutTests\"`."
}

## Stage 6 - Implement

{
  "summary": "Applied four cosmetic fixes across 4 files: (1) ActivityColumn.axaml — added VerticalAlignment=\"Center\" to the ACTIVITY panelTitle TextBlock so it aligns with the Reveal button (matching QueuePanel pattern). (2) TaskDetailPanel.axaml — changed Reveal button Margin from \"0,0,6,0\" to \"6,0,6,0\" to restore the left-side gap between the file-path TextBlock and the button. (3) MainWindow.axaml — added Classes=\"gripDot\" to each GripDot1/2/3 Border and collapsed three identical pointerover selectors into one Border.gripDot class-based selector. (4) TaskDetailRemoveButtonLayoutTests.cs — updated stale class doc wording from \"*,Auto,Auto Grid\" to \"DockPanel\" and assertion message to reference \"DockPanel\" instead of just \"Border\". All 1949 tests pass (0 failures, 12 pre-existing skips)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verification passed: 1949 tests, 0 failures, 12 pre-existing skips. All four cosmetic fixes (ACTIVITY title vertical centering, attachment Reveal button left margin, grip-dot DRY refactor, and test message updates) are confirmed working and all existing tests stay green." }

## Stage 9 - Verify

{
  "summary": "Applied four cosmetic UI polish fixes: (1) ACTIVITY panelTitle VerticalAlignment=\"Center\" in ActivityColumn.axaml for vertical centering with Reveal button; (2) Reveal button Margin changed to \"6,0,6,0\" in TaskDetailPanel.axaml to restore gap between file-path text and button; (3) grip-dot pointerover styles DRY'd via shared Border.gripDot class selector in MainWindow.axaml; (4) updated stale Grid→DockPanel wording in TaskDetailRemoveButtonLayoutTests.cs assertion messages. All 1949 tests pass (0 failures, 12 pre-existing skips).",
  "commitMessages": [
    "fix: vertically center ACTIVITY title and restore attachment path-to-Reveal gap",
    "fix(ui): align ACTIVITY panel title with Reveal button and add spacing to attachment file paths",
    "refactor: collapse duplicate grip-dot pointerover styles into shared class selector",
    "fix: correct stale Grid references in layout test assertion messages",
    "style: apply cosmetic alignment and spacing polish across ActivityColumn, TaskDetailPanel, and MainWindow"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

