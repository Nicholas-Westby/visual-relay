# UI polish: ACTIVITY title alignment, attachment row spacing, minor cleanups

Small cosmetic follow-ups surfaced while reviewing the recent UI changes
(`fix-right-panel-collapsed-state` `54cff3d`, `remove-button-sometimes-cut-off` `f7ef26e`,
`make-resizer-look-nicer` `7808033`). None are functional bugs; each is a low-risk polish item.

## 1. ACTIVITY panel title is not vertically centered
In `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml`, the header was reordered so the
`panelTitle` `TextBlock` ("ACTIVITY") now sits in the same `Auto`-height row as the taller
"Reveal" button (and the toggle button), but — unlike the Queue panel's title, which sets
`VerticalAlignment="Center"` — the ACTIVITY title has no `VerticalAlignment`, so it can render
top-aligned relative to the button. **Fix:** add `VerticalAlignment="Center"` to the ACTIVITY
`panelTitle` `TextBlock` so it matches the Queue panel and aligns with the Reveal button.

## 2. Attachment path text butts against the "Reveal" button
In `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`, the attachment item template was
changed from a Grid (which had `ColumnSpacing="6"`) to a `DockPanel`. The `Reveal` button now has
`Margin="0,0,6,0"` (gap only on its right), so the trimmed file-path `TextBlock` (the fill child)
sits directly against the left edge of `Reveal` with no gap. **Fix:** give the `Reveal` button
`Margin="6,0,6,0"` to restore the ~6px gap between the path text and the button. (The ellipsis
already prevents overlap; this is spacing only.)

## 3. Minor cleanups (optional, low priority)
- In `ActivityColumn.axaml` (the splitter from `make-resizer-look-nicer`), the three grip dots
  (`GripDot1/2/3`) each have an identical `:pointerover` setter. Collapse them into a single
  shared `Classes`-based selector to DRY the styles (keep the visual result identical). Only do
  this if it doesn't fight the existing tests that count named borders — otherwise leave as-is.
- In `tests/VisualRelay.Tests/TaskDetailRemoveButtonLayoutTests.cs`, the assertion failure
  messages still say "Grid"/cite line numbers from before the DockPanel change; update the
  wording to reference the DockPanel so a future failure isn't misleading. (Message text only —
  do not weaken the assertions.)

## How to verify
- The "ACTIVITY" header title is vertically centered, level with the Reveal button.
- In the attachments list, there is a visible gap between a (long, ellipsized) file path and the
  Reveal button.
- Existing layout/affordance tests stay green; visual result of the splitter is unchanged.

## Constraints
- VR is general-purpose; no project-specific assumptions. Files ≤300 lines; `./visual-relay check`
  green; Conventional Commit subjects. This is cosmetic only — do not change collapse/resize
  behavior, the DockPanel structure, or the splitter's appearance/hit area.
