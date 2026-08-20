# Task: Show event times in the Run Log

The Run Log is the app's live diagnostic feed, and it is the only sink in the
project that never says *when* anything happened. A stage that started ten
seconds ago and one that started forty minutes ago render identically, so a
watcher cannot tell a stalled run from a fast one, cannot see the gap between two
events, and cannot line the log up against anything else on their screen.

The data is already on the record and is already trusted for other purposes.
`RelayEvent.Timestamp` is the first positional field of the record, it is what the
view model sorts by when replaying a task's history, and it is what drives the
stage board's elapsed-time segments. The two command-line sinks in this repo both
print it as `HH:mm:ss`. Only the GUI throws it away.

This is the follow-up the previous run-log task explicitly deferred: adding
timestamps is listed under *Out of scope* in
`llm-tasks/completed/colour-run-log-attention-rows/DONE-colour-run-log-attention-rows.md:66`.

### Evidence (2026-08-20)

- `src/VisualRelay.Domain/RelayEvent.cs:4` declares `DateTimeOffset Timestamp` as
  the first positional parameter of the record. The two computed display strings
  the view actually binds — `DisplayLine` at lines **22-23** and `DetailLine` at
  lines **25-28** — never reference it.
- `src/VisualRelay.App/Views/Controls/RunLogView.axaml` (94 lines) contains all
  three row templates: the `HeartbeatGroupRow` header at lines **26-35**, the
  expanded grouped-member `RelayEvent` template at lines **52-68**, and the
  `SingleEventRow` template at lines **75-91**. None of the three mentions
  `Timestamp`.
- A grep for `Timestamp` across `src/VisualRelay.App/Views/` and
  `src/VisualRelay.App/Styles/` returns nothing at all. The property reaches the
  view — `IRunLogRow.Event` at
  `src/VisualRelay.App/ViewModels/RunLogRows/IRunLogRow.cs:19` — and is dropped.
- Every other sink already prints it, in the same format:
  `tools/VisualRelay.DrainQueue/ConsoleRelayEventSink.cs:22` writes
  `$"[{taskId}] {relayEvent.Timestamp:HH:mm:ss} {relayEvent.DisplayLine}{detail}"`,
  and `tools/VisualRelay.RunTask/Program.cs:44` writes
  `$"{relayEvent.Timestamp:HH:mm:ss} {relayEvent.DisplayLine}{detail}"`. The GUI
  is strictly worse than the CLI here.
- The timestamp is real on both population paths, not a placeholder. Live events
  stamp `DateTimeOffset.UtcNow` at emit (for example
  `src/VisualRelay.Core/Execution/ProcessRunners.Escalation.cs:24`), and replayed
  history rebuilds each event from the on-disk report's own time —
  `src/VisualRelay.Core/Tasks/RelayRunHistory.cs:32-45` passes `stage.Timestamp`
  into the `RelayEvent` constructor, sourced from `ReadTimestamp` at line **147**.
- The app already relies on the value being correct:
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs:62` orders the
  replay by `item.Timestamp`, `MainWindowViewModel.Helpers.cs:112` feeds it to
  `stage.MarkRunning`, and `MainWindowViewModel.StageMetrics.cs:74` and **:81**
  open and close the elapsed-time segments with it.
- `RelayEvent` is never serialised. History is reconstructed from
  `stage*-attempt*.report.json` by `RelayRunHistory.ReadTaskEvents`, so adding a
  computed property to the record cannot change any on-disk format.
- Both row implementations already expose the event the label needs:
  `SingleEventRow.cs:20` (`public RelayEvent Event { get; }`) and
  `HeartbeatGroupRow.cs:59` (`public RelayEvent Event => _members[0];`, the
  newest member). `HeartbeatGroupRow.InsertNewest` already raises
  `OnPropertyChanged(nameof(Event))` at line **88**, so a merged group's label
  refreshes for free.

Width budget, measured by pixel scan on the real 1440x900 capture
`.relay/colour-run-log-attention-rows/visual-review/main.png` (rendered with
`ActivityColumnWidth = 360`, `tools/VisualRelay.Screenshots/Program.cs:101`):

- The Run Log content column runs from **x=1096 to x=1397**, i.e. **302 px** of
  usable width per row.
- The header line is the row with room to spare. `s3/balanced stage_start`
  (23 characters) measures **192 px**, leaving **110 px** of empty space to its
  right. `s2/cheap stage_report` (21 characters) measures **175 px**. That works
  out at **8.35 px per character** — Menlo SemiBold at the inherited Fluent
  default of 14 px, since `TextBlock.logHeader`
  (`src/VisualRelay.App/Styles/VisualRelayTheme.axaml:223-229`) sets no
  `FontSize`.
- The detail line is the row with *no* room. On the `stage_done` row it measures
  **x=1097 to x=1397 (301 px)** — it fills the column exactly and ellipsises, and
  the truncated `nam…` is visible in the capture.
- An `HH:mm:ss` label is 8 characters. In Menlo at `FontSize="11"` that is
  **≈53 px**; with an 8 px gap, **≈61 px** — comfortably inside the 110 px of dead
  space on the header row, and still inside the 302 px column for the longest
  header the app can produce today, `s10/frontier stage_escalated`
  (28 characters ≈ 233 px; 233 + 61 = 294 px).

Tests that pin current behaviour:

- `tests/VisualRelay.Tests/RunLogHeaderAttentionTests.cs:83` locates the header
  with `.Single(tb => tb.Text == row.DisplayLine)` — an **exact** string equality
  against `DisplayLine`, over a `.Where(tb => tb.GetType() == typeof(TextBlock))`
  filter. It survives untouched **only if** the timestamp goes in its own
  `TextBlock` and the header's `Text` binding stays exactly `{Binding DisplayLine}`.
  Merging a time into the header's `Text` breaks both tests in that file.
- `tests/VisualRelay.Tests/RelayEventTests.cs:37` asserts
  `Assert.Equal("s10/frontier stage_escalated", escalation.DisplayLine)`. It
  survives as long as `DisplayLine` is left alone.
- `tests/VisualRelay.Tests/RunLogGroupingRenderTests.cs` collects every visible
  `TextBlock`/`SelectableTextBlock` and asserts `Contains` on substrings (lines
  **81**, **84**, **120**, **157**) plus `DoesNotContain` on the `"×"` count
  marker (lines **123** and **159**). An `HH:mm:ss` label contains no `×`, so all
  three tests survive.
- `tests/VisualRelay.Tests/ActivityColumnTabsUiTests.BackCompat.cs:31` only
  asserts the `ListBox` exists and has items. It survives.

### What to build

Render the event's wall-clock time on every Run Log row, in the user's local time,
formatted `HH:mm:ss` to match the two existing console sinks.

- Add a computed `TimeLabel` to `RelayEvent`, alongside `DisplayLine` and
  `DetailLine`, returning the timestamp converted to local time and formatted
  `HH:mm:ss` with the invariant culture. Convert with `ToLocalTime()` — live
  events are stamped `DateTimeOffset.UtcNow`, and a desktop log has to match the
  clock in the user's menu bar. Do not change `DisplayLine` or `DetailLine`.
- Bind it in all three templates of `RunLogView.axaml`: `{Binding Event.TimeLabel}`
  in the `HeartbeatGroupRow` header (lines 26-35) and the `SingleEventRow` header
  (lines 75-91), and `{Binding TimeLabel}` in the expanded grouped-member template
  (lines 52-68), whose data context is a bare `RelayEvent`. The expanded members
  are where per-event times matter most — that is the view you open to find out
  when a 30× heartbeat run actually started and stopped.
- Put the label in its **own** `TextBlock`, right-aligned in an `Auto` column on
  the header row, with the header line keeping the `*` column. Right-aligned, not
  a left gutter: it consumes the 110 px of dead space the header row already has,
  it lines up identically across grouped and single rows (which differ by the
  chevron gutter at the *left*, `RunLogView.axaml:20-25`), and leaving the header
  in the star column means its existing `MaxLines="1"` /
  `TextTrimming="CharacterEllipsis"` behaviour is unchanged. Update the affected
  `Grid.ColumnSpan` values on the detail rows so they still span the full width.
- Follow the pattern commit `eb5a811` just established: define a
  `TextBlock.logTime` style in `VisualRelayTheme.axaml` next to the existing
  `.logHeader` rules rather than repeating inline attributes in three templates.
  Set `FontFamily="Menlo,Consolas,monospace"` so the digits stay fixed-width and
  the times align into a clean column, `FontSize="11"`, and a muted foreground
  dimmer than the header — the existing detail grey `#7F8794` is the right weight.
  The time is context, not the headline; it must not compete with the blue
  `#53B7F4` header or the attention red `#F36F63`.
- Do not apply `Classes.attention` to the time label. A warn row's *header* turns
  red; its timestamp stays grey.
- Add a unit test for `TimeLabel` in `RelayEventTests.cs`, and a headless render
  test in the style of `RunLogHeaderAttentionTests.cs` asserting that the rendered
  Run Log contains the expected `HH:mm:ss` text for a seeded event. Construct the
  `RunLogView` directly with a minimal view-model — do not boot a `MainWindow`.

### Out of scope

- Do not change `DisplayLine`, `DetailLine`, `IsAttention`, or any positional
  field of the `RelayEvent` record. `TimeLabel` is purely additive.
- Do not change `IRunLogRow`, `SingleEventRow`, `HeartbeatGroupRow`, or
  `RunLogGrouper`. The view binds through the existing `Event` property; no new
  interface member is needed.
- Do not touch `tools/VisualRelay.Screenshots/Program.cs`. The six events it seeds
  at lines **185-196** already carry distinct timestamps and are all the change
  needs to be visible.
- Do not make the screenshot harness clock deterministic. The seeded times are
  `now - 2/7/22/24/30/45s` and therefore shift between runs, but nothing in this
  repo compares screenshots against a golden image, and every property a reviewer
  checks (six labels present, fixed width, right-aligned, decreasing top to
  bottom) is stable across runs. A fake-clock or injected-clock refactor of the
  screenshot tool is a separate task.
- Do not change `ConsoleRelayEventSink.cs` or `RunTask/Program.cs`. Those print
  UTC because they format the raw `DateTimeOffset`; harmonising them on local time
  is a separate task and must not be bundled here.
- Do not add relative/"ago" times, date parts, sub-second precision, a
  time-zone suffix, or a user-configurable format. One fixed `HH:mm:ss`.
- Do not add timestamps to the Commands tab's `TraceEntries`, to task cards, or to
  the stage board.
- Do not change the detail line's colour, font, `MaxLines`, or the
  `.logDetail.attention` wrapping rules at `VisualRelayTheme.axaml:213-221`.
- Do not touch `ActivityColumn.axaml` or the Activity column's tab structure.

### Constraints

- Hard guard: no `.cs`/`.axaml` under `src/`, `tests/` or `tools/` may exceed 300
  lines (`tools/VisualRelay.Guards/FileSizeGuard.cs:13`, run by
  `./visual-relay check`). Current counts for every file this change touches:
  `src/VisualRelay.Domain/RelayEvent.cs` **48** (252 spare),
  `src/VisualRelay.App/Views/Controls/RunLogView.axaml` **94** (206 spare),
  `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` **233** (67 spare — the
  tightest file here, so keep the new style to a handful of setters),
  `tests/VisualRelay.Tests/RelayEventTests.cs` **43** (257 spare). For reference,
  the files that must keep working but should not be edited:
  `tests/VisualRelay.Tests/RunLogHeaderAttentionTests.cs` **85**,
  `tests/VisualRelay.Tests/RunLogGroupingRenderTests.cs` **173**,
  `tools/VisualRelay.Screenshots/Program.cs` **219**.
- The header `TextBlock`'s `Text` must remain exactly `{Binding DisplayLine}` (and
  the group header's existing `MultiBinding` with `{}{0} ×{1}`), or
  `RunLogHeaderAttentionTests.cs:83` fails on `.Single(...)`.
- Headless UI tests use `[AvaloniaFact]`; `HeadlessUnitTestSession` is banned by
  BannedApiAnalyzers. New UI tests instantiate `RunLogView` plus the minimal
  view-model slice, never a whole `MainWindow`.
- The 1440x900 capture must show, in the Run Log panel on the right: **six**
  `HH:mm:ss` labels, one per row, in fixed-width digits forming a right-aligned
  column flush to the panel's right edge at roughly **x=1340-1397**; grey and
  visibly smaller than the header text beside them; and **decreasing from top to
  bottom**, since the list is newest-first and the seeded events span about 43
  seconds. Every header — `s3/balanced stage_start`, `s3/balanced trace`,
  `s2/cheap stage_done`, `s2/cheap stage_report`, `s2/cheap tests_red`,
  `s1/cheap stage_done` — must still render in full with **no** ellipsis. The
  `tests_red` header must still be red. The detail lines below them must be
  unchanged, including the existing `nam…` truncation.
