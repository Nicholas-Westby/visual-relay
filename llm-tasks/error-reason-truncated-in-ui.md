# Failure reasons are truncated everywhere, so you can't read why a stage failed

When a stage fails, the only place the full reason exists is on disk
(`.relay/<task>/stage*-attempt*.report.json` → `result.error_message`, and the
`.relay/<task>/NEEDS-REVIEW` marker). Every surface that shows it in the GUI clips it to one
line with no way to see the rest, so a flagged run is undiagnosable from the app. Concretely,
a real failure reads `swival exit 1: Error: LLM call failed (model: cheap-kimi): ... Connection
error.` but the UI only ever shows `… Error: LLM call fai…`.

Three surfaces all clip and offer no escape hatch:

- **Queue card review reason** — `src/VisualRelay.App/Views/Controls/QueuePanel.axaml:98`
  (`Text="{Binding ReviewReason}"`, `MaxLines="1"` + `TextTrimming="CharacterEllipsis"`).
  Bound to `TaskRowViewModel.cs:36` → `RelayTaskItem.ReviewReason`.
- **Run log** — `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml:36` and `:42`
  (`DisplayLine` and `DetailLine`, both `MaxLines="1"` + ellipsis). The `reason: …` text is
  built in `src/VisualRelay.Domain/RelayEvent.cs:18` (`DetailLine`).
- **Header status chip** — `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:30`
  (`MaxWidth="260"`, `MaxLines="1"` + ellipsis).

## Recommended fix

Give each clipped surface a way to reveal the full text without leaving the app. Pick the
lightest option that fits each control, applied consistently:

- Add a `ToolTip.Tip` bound to the same full text on the clipped `TextBlock`s, so hover shows
  the untruncated reason. This is the minimum and covers the queue card, run-log detail line,
  and the status chip.
- For the run log, also allow the `DetailLine` to wrap (`TextWrapping="Wrap"`, drop
  `MaxLines="1"`) for `warn`/error-level events so a failure reason is readable inline while
  routine events stay compact.
- Make the revealed text selectable/copyable (e.g. `SelectableTextBlock` or a "Copy reason"
  affordance) so the reason can be shared from a remote session.

Keep routine, non-error lines visually compact — only failure reasons need the extra room.
This is presentation only; the full reason already exists in `ReviewReason` /
`RelayEvent.Data["reason"]`, nothing new needs to be read from disk.

## Sequencing

- Independent (presentation only). If `error-message-resolution-hints.md` lands first, the text
  this reveals is the hint-enriched reason; if not, it reveals the raw reason and picks up the
  hint automatically once that task lands. Pairs with `surface-stage-error-in-detail-pane.md`
  (same reason text, different surface).

## Done when

- A flagged task's full reason is reachable from the queue card, the run log, and the header
  chip (hover and/or wrap), not just the truncated prefix.
- The reason text can be selected/copied from the UI.
- Routine non-error log lines remain single-line and compact.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
