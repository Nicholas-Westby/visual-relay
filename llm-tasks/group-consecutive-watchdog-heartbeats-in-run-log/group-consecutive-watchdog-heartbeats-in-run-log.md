# Run Log: Collapse Consecutive watchdog_heartbeat Entries into One Live Group Row

During a long stage the Run Log tab fills with dozens of near-identical rows — observed
2026-07-07 during a ~2h Review stage: an unbroken wall of `s7/frontier watchdog_heartbeat`
entries (each differing only in `silenceMs`/`deadlineMs`) drowning out the events that matter.
Collapse each contiguous run of heartbeats into a single row that updates live, e.g.
`s7/frontier watchdog_heartbeat ×30`, expandable on click. **Display-layer only — the
underlying events, their storage, and their emission are not to change.**

## Verified architecture (anchors)

- **Event model** — `src/VisualRelay.Domain/RelayEvent.cs`: immutable record with
  `Timestamp, Level, EventName, RunId, RootPath, TaskId, StageNumber, Tier, Attempt, Data`;
  computed `DisplayLine => StageNumber is null ? EventName : $"s{StageNumber}/{Tier ?? "?"} {EventName}"`,
  `DetailLine` (joined `Data` pairs), `IsAttention => Level is "warn" or "error"`.
- **Heartbeat emission** — `ProcessRunners.RunAsync.cs`, the `ActivityWatchdog` `onHeartbeat`
  callback publishes `new RelayEvent(…, "debug", "watchdog_heartbeat", …, Data: {["message"] = msg})`.
  Heartbeats are always level `"debug"`.
- **View** — `src/VisualRelay.App/Views/Controls/RunLogView.axaml`: a `ListBox` bound to
  `{Binding Events}` with one `DataTemplate DataType="{x:Type domain:RelayEvent}"` rendering
  `DisplayLine` (blue header) + `DetailLine` (grey, `Classes.attention="{Binding IsAttention}"`).
- **Collection** — `MainWindowViewModel.Events` is `ObservableCollection<RelayEvent>`,
  **newest-first**. Exactly two code paths populate it (all flows converge here):
  1. incremental append in `MainWindowViewModel.Helpers.cs`: `Events.Insert(0, relayEvent)`
     (gated by the selected-task check and `_selectedStageFilter`);
  2. bulk rebuild `ApplyLogFilter()` (same file): `Events.Clear()` then `Add` of every
     `_allTaskEvents.Where(IsInSelectedStage)` — used by stage-filter changes and by
     task selection / run-history load (`MainWindowViewModel.RunHistory.cs` ends with
     `ApplyLogFilter()`).
- **Flat storage** — `_allTaskEvents` (a `List<RelayEvent>` on the VM), the on-disk run.log,
  and `RelayRunHistory.ReadTaskEvents` are the source of truth and stay flat and untouched.
- No control-API endpoint reads `Events` (checked: `ControlApi.Tabs.cs` only selects tabs), so
  grouping has no automation-surface impact beyond what screenshots show.

## What to build

1. **A grouped row projection for the Run Log.** The `ListBox` renders rows where each row is
   either a single `RelayEvent` (everything non-heartbeat, exactly as today) or a
   **heartbeat group**: a maximal run of *adjacent* events (in display order) that all have
   `EventName == "watchdog_heartbeat"` and identical `(StageNumber, Tier)` — equivalently,
   identical `DisplayLine`. Any other event between two heartbeats splits them into separate
   groups; a tier or stage change likewise starts a new group. Whether this means changing
   `Events`' element type to a small row view-model or binding the `ListBox` to a derived
   grouped collection is the implementer's choice — but the grouping logic must be one shared,
   VM-testable function used by **both** population paths above (incremental insert and
   `ApplyLogFilter` rebuild), so live drains, stage-filter flips, and finished-task history
   all group identically.
2. **Group row rendering.** Header = the shared `DisplayLine` plus a count indicator (e.g.
   `s7/frontier watchdog_heartbeat ×30`); detail line = the **newest** member's `DetailLine`
   (the values change every beat — showing the oldest would display stale
   `silenceMs`/`deadlineMs`). A run of length 1 renders as a plain single row, no count, no
   expander. Keep the existing monospace look for both row kinds.
3. **Live in-place growth.** When a new heartbeat arrives and the newest row is a matching
   group (same stage/tier, nothing between), it merges into that row: count increments and the
   shown detail updates — no new row is added, and newest-first ordering is preserved. Only
   the insertion-adjacent row is ever merged into; never reach past a non-matching row.
4. **Expand/collapse.** A click affordance on the group row (chevron, button, or row click)
   expands the group inline into its individual member rows, rendered like today's single
   rows, and collapses back. Expansion state is per-group; it is acceptable for a bulk rebuild
   (stage-filter change, task switch) to reset expansion, but a live count increment must not
   collapse an expanded group.
5. **Scope and storage discipline.** Run Log tab only — the Commands/System/Output tabs and
   `TraceEntries` are untouched. `RelayEvent`, every emission site, `_allTaskEvents`, run.log,
   and `RelayRunHistory` are unchanged: grouping happens strictly at the point where events
   become visible rows. Only `EventName == "watchdog_heartbeat"` ever groups — warn/error
   events must be impossible to swallow into a group (heartbeats are emitted `"debug"`; keep
   the rule keyed on `EventName`, not on level).
6. **Tests.** VM-level tests on the shared projection (no UI boot needed for these):
   - a run of matching heartbeats collapses to one group with the right count;
   - an interleaved non-heartbeat (e.g. a `trace` event) splits the run into two groups;
   - a tier change (`balanced` → `frontier`) and a stage-number change each start a new group;
   - a new arrival grows the newest matching group in place (row count stable, count +1,
     detail = newest);
   - singles render as plain rows; non-heartbeat events are byte-identical to today's rows;
   - expanding a group yields its members in order; collapse restores the single row;
   - `ApplyLogFilter` rebuild (stage filter on/off) produces the same grouping as the
     incremental path fed the same events.
   Plus one lightweight headless render test: `RunLogView` showing a grouped row renders the
   count header (construct the control directly — do not boot `MainWindow`). If any existing
   test asserts `Events`' element type or contents, update it deliberately to the row
   projection — do not weaken what it proves.

## Done when

- During an active drain, a stage's heartbeat wall renders as a single live-updating
  `… watchdog_heartbeat ×N` row per contiguous run, splitting where stage/tier changes or
  another event interleaves; clicking expands to the individual entries.
- Selecting a finished task or toggling the stage filter shows the same grouped shape.
- run.log and `_allTaskEvents` contain exactly the same flat events as before the change.
- All tests above pass; full suite green; `./visual-relay check` passes.

## Guardrails

- No changes to `RelayEvent`, event emission, event persistence, or `RelayRunHistory`.
- No changes to newest-first ordering, the selected-task gate, or `IsInSelectedStage`
  semantics — the filter keeps operating on flat events, grouping applies after it.
- UI/VM layer only; buttons via the centralized button components
  (`Views/Controls/Buttons/`, enforced by `ButtonsCentralizationTests`).
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs; files stay
  under the 300-line guard.
