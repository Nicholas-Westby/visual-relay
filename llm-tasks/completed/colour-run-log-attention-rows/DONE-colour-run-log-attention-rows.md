# Task: Colour run-log rows that need attention

The Run Log is the app's primary diagnostic surface, but a warning or an error is
typographically identical to a routine `trace`. Every row header renders in the
same blue, so finding trouble means reading every line.

The data needed to fix this is already on the view. `RelayEvent.IsAttention` is
computed, is already bound into the template, and is spent on a style that
changes only line wrapping. Nothing changes colour.

### Evidence (2026-08-20)

- `src/VisualRelay.Domain/RelayEvent.cs:20` defines
  `public bool IsAttention => Level is "warn" or "error";`.
- `src/VisualRelay.App/Views/Controls/RunLogView.axaml` has three row templates —
  `HeartbeatGroupRow` (line 13), a grouped-member `RelayEvent` (line 55), and
  `SingleEventRow` (line 81). All three hard-code the header colour as a literal:
  `Foreground="#53B7F4"` at lines **29**, **62** and **88**. None of the three is
  a binding.
- `Classes.attention="{Binding IsAttention}"` is already applied, but only to the
  *detail* line, at lines **69** and **95**. The header never sees it.
- That class does nothing visual. `src/VisualRelay.App/Styles/VisualRelayTheme.axaml:217-221`
  defines `SelectableTextBlock.logDetail.attention` with exactly three setters —
  `MaxLines=0`, `TextTrimming=None`, `TextWrapping=Wrap`. There is no `Foreground`
  setter anywhere in either `.logDetail` rule.
- Confirmed on a real render: in `.relay/show-stage-tier-on-stage-cards/visual-review/main.png`
  the `warn` row `s2/cheap tests_red` and the `info` row `s3/balanced stage_start`
  quantise to `#4FADE7` and `#4FAEE8` — the same blue to within one bit per channel.
  The only present-day signal that a row is a warning is that its detail line
  happens to wrap.
- The app already has a red for exactly this meaning: `#F36F63`, used as
  `FlaggedBrush` at `src/VisualRelay.App/ViewModels/StageRowViewModel.cs:13`.
- No test pins any run-log foreground. `RunLogGroupingRenderTests.cs` and
  `RunLogGroupingRowTests.cs` assert row structure and `IsAttention` as a bool;
  neither contains the string `Foreground`.

### What to build

Make a run-log row whose event is `warn` or `error` visually distinct from a
routine row, by colouring its **header** line with the existing attention red
`#F36F63` instead of `#53B7F4`.

- Drive it from the existing `IsAttention`. Do not add a new domain property.
- Prefer a style selector over three copy-pasted inline literals. A
  `TextBlock.logHeader` / `TextBlock.logHeader.attention` pair in
  `VisualRelayTheme.axaml`, with the templates applying
  `Classes="logHeader" Classes.attention="{Binding IsAttention}"`, removes the
  duplicated literal at the same time. Keep `FontFamily`, `FontWeight`,
  `MaxLines` and `TextTrimming` behaviour exactly as they are today.
- `HeartbeatGroupRow.IsAttention` is hard-coded `false`
  (`src/VisualRelay.App/ViewModels/RunLogRows/HeartbeatGroupRow.cs:57`), so the
  grouped header at line 29 will simply never take the attention colour. That is
  acceptable and expected — wire it consistently anyway rather than special-casing
  it, so the three templates stay uniform.
- Add tests in the style of the existing headless render tests: assert that a
  `warn` event's header resolves to the attention brush and an `info` event's
  header does not.

### Out of scope

- Do not distinguish `warn` from `error` — both are "attention" and both take the
  same red. A per-level palette is a separate task.
- Do not change the detail-line colour `#7F8794`, or the existing
  `.logDetail.attention` wrapping behaviour.
- Do not change `RelayEvent`, `IRunLogRow`, or any Control API shape.
- Do not add timestamps, icons, badges, or background fills to run-log rows.
- Do not touch the Activity column's tab structure or `ActivityColumn.axaml`.

### Constraints

- Hard guard: no `.cs`/`.axaml` under src/, tests/ or tools/ may exceed 300 lines.
  Current: `RunLogView.axaml` 103, `VisualRelayTheme.axaml` 222,
  `RelayEvent.cs` 48. All have headroom.
- `tools/VisualRelay.Screenshots/Program.cs:193` already seeds exactly one `warn`
  event (`tests_red`) among otherwise-`info` events, so the change is visible in
  the standard render with no harness change. Do not modify the harness.
