# Archive: order by completion time, with per-day dividers

The ARCHIVE panel lists completed tasks but in a near-meaningless order and gives
no sense of *when* work shipped. Sort archived tasks by when they were actually
completed (newest first) and insert a per-day heading so it is obvious which tasks
finished on which day.

## Current state (researched)

`RelayTaskRepository.ListCompletedAsync` (`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs`)
gathers every archived `DONE-*.md` (top-level, folder, and under `completed/batch-n/`)
and returns them ordered by:

```csharp
.OrderByDescending(task => File.GetLastWriteTimeUtc(task.MarkdownPath))
```

That key is wrong. Across the 165 archived tasks on disk the markdown mtime clusters
at bulk-filesystem instants — four unrelated tasks all read `2026-06-15T00:41:04` —
because this repo is shared with the VM (host↔VM sync) and gets bulk `git checkout`s
that reset mtimes. It reflects the last sync, not completion. File mtimes under
`.relay/` are corrupted the same way (many pinned to a single `2026-06-16T08:53:44`).

The reliable signal already exists **and is already loaded**: each
`.relay/<id>/stage*-attempt*.report.json` carries a content `"timestamp"` (ISO-8601,
UTC), parsed today into `StageRunMetric.Timestamp` by `RelayRunHistory.ReadTaskMetric`.
`ListCompletedAsync` already calls `AttachRunMetrics`, so every archived task's stage
timestamps are in hand — they are simply never used to sort. Being inside the JSON,
they survive mtime resets; each task gets a distinct, real completion instant.

Coverage of the 165 archived tasks: **113** have report timestamps; **9** have a
`.relay/<id>/` dir but no report timestamp; **43** have no `.relay/<id>/` dir at all
(older runs, or tasks not completed by Relay). So a fallback chain is needed for the
52 (~31%) without run metadata.

The list renders in `QueuePanel.axaml` through a single `ListBox`
(`ItemsSource="{Binding Tasks}"`, one `DataTemplate` for `TaskRowViewModel`) shared by
QUEUE and ARCHIVE; `MainWindowViewModel.ReloadTaskListAsync`
(`MainWindowViewModel.Helpers.cs`) swaps the collection's contents per mode. There is
**no clock abstraction** in the codebase — `DateTimeOffset.UtcNow` is called inline. A
pinned git seam already exists: `IGitInvoker` / `GitInvoker`
(`src/VisualRelay.Core/Execution/GitInvoker.cs`), `RunAsync(root, args, ct, timeout?)`
returning `(int ExitCode, string Output, bool TimedOut)`, with an `IGitInvoker`
interface and a `GitInvoker(binaryPath)` test constructor.

## What to build

### 1. Resolve a real completion time (`CompletedAt`)

Add `DateTimeOffset? CompletedAt` to `RelayTaskItem` (default `null`; populated only for
archived items). Resolve it with a **four-tier fallback chain — first tier that yields a
value wins.** Put the logic in a new `CompletionTimeResolver` in its own file
(`RelayTaskRepository.cs` is already 287/300 lines):

1. **Run metadata** — `max(StageRunMetric.Timestamp)` across the task's stage reports
   (the `"timestamp"` field in `.relay/<id>/stage*-attempt*.report.json`). Already loaded
   via `AttachRunMetrics`; no new I/O.
2. **Newest `.relay/<id>/` mtime** — when a `.relay/<id>/` dir exists but no report carries
   a timestamp, use the newest last-write time of any file under it.
3. **Git retirement commit** — when there is no `.relay/<id>/` dir, the committer date of
   the last commit touching the DONE markdown:
   `log --follow -1 --format=%cI -- <MarkdownPath>` through the injected `IGitInvoker`
   (≈5 s timeout). `--follow` resolves the `<id>.md → DONE-<id>.md` rename and the move
   into `completed/…` (verified for both flat and nested paths). Any non-zero exit, empty
   output, timeout, or missing git → fall through.
4. **Markdown mtime** — `File.GetLastWriteTimeUtc(MarkdownPath)`, last resort (a task
   hand-renamed to `DONE-`, never run by Relay, never committed).

Tiers 1 and 3 are authoritative; tiers 2 and 4 are filesystem mtimes and therefore
approximate in this repo — acceptable as fallbacks.

Wiring:
- Give `RelayTaskRepository` an **optional** `IGitInvoker? gitInvoker = null` constructor
  parameter (when null, tier 3 is skipped and resolution falls to tier 4 — keeps existing
  call sites and the pending path unaffected). The app passes its shared `GitInvoker`;
  tests pass a fake or `GitInvoker(binaryPath)`.
- Resolve `CompletedAt` **in `ListCompletedAsync` only** — tier 3's git probe must never
  run for pending tasks. Tier 1 reuses the `TaskRunMetric` already read by
  `AttachRunMetrics` (no second read); tiers 2–4 run only for archived items still
  unresolved.
- Order `ListCompletedAsync` by `CompletedAt` **descending**, tie-broken by `Id` (ordinal)
  for determinism. Delete the `File.GetLastWriteTimeUtc` sort. This finally makes the
  existing "archive is sorted by completion time" comment in `ReloadTaskListAsync` true.

### 2. Per-day dividers

- Add a **pure** `ArchiveDayGrouping` helper in its own file: given the
  completion-ordered items and a reference `today` (local `DateOnly`), return the heading
  label for the **first** row of each local-calendar day and `null` for the rest. Label =
  `Today`, `Yesterday`, else `dddd, MMMM d, yyyy` (e.g. `Wednesday, June 17, 2026`,
  `CultureInfo.CurrentCulture`). Day key = `CompletedAt.Value.ToLocalTime().Date` (report
  timestamps are UTC). Inject `today` (production: `DateTimeOffset.Now`; tests: a fixed
  value) — no clock abstraction, matching the codebase.
- `TaskRowViewModel` gains a `DayHeader` string (empty = none). `ReloadTaskListAsync` sets
  it from the helper after building the archive rows; QUEUE rows leave it empty.
- In `QueuePanel.axaml`, render `DayHeader` as a **muted left heading** above the card,
  `IsVisible` only when non-empty (small, semibold, muted — e.g. `#5A6270` — matching the
  existing panel-title aesthetic). Keep `Tasks` as `ObservableCollection<TaskRowViewModel>`
  — **no new item type**, so selection is unaffected (the heading is part of the card row,
  not a separately selectable row).
- **No** per-card completion time; cards keep `MetricsLine` (duration + cost).

## Done when

- ARCHIVE lists completed tasks newest-completed first, grouped under
  `Today` / `Yesterday` / full-date headings; only the first task of each local day shows
  the heading.
- A task with `.relay` reports orders by its run metadata; a Relay-less hand-`DONE-`'d task
  still slots in by git commit time, and by markdown mtime if uncommitted; resolution never
  throws when git is missing or the directory is not a repo.
- QUEUE is unchanged — no headings, existing manual/alphabetical order preserved.
- Tests first, covering: each resolver tier in isolation **and** that a later tier fires
  only when earlier ones are empty (tier 3 uses a temp git repo + injected `IGitInvoker`);
  ordering by `CompletedAt` desc with `Id` tie-break; the pure grouping with a fixed
  `today` (including a UTC-evening timestamp that must land on the correct *local* day, and
  the Today/Yesterday boundary). Extend `RelayTaskRepositoryTests` /
  `RelayTaskRepositoryDoneFolderTests`.
- `./visual-relay check` green; C#/XAML files under 300 lines (extract
  `CompletionTimeResolver` and `ArchiveDayGrouping` into their own files); Conventional
  Commit subjects.

## Decisions (settled)

- **Fallback order is fixed:** run metadata → newest `.relay/<id>/` mtime → git commit time
  → markdown mtime. Do not reorder.
- **No new write-side metadata.** Read existing sources only; do not stamp a completion
  time into the markdown or the status record — the delicate retirement/commit path
  (`TaskCompletionArchive.RetireAsync`) stays untouched.
- **Labels:** `Today` / `Yesterday` / `dddd, MMMM d, yyyy`. **Style:** muted left heading.
  **No** per-card time.
- **Local day** for grouping; timestamps are UTC and must be converted.
- Dividers are independent of `completed/batch-n/` storage grouping — a day may span
  batches and a batch may span days.
- The git tier runs only for tasks unresolved by tiers 1–2 (~43 today, shrinking as new
  tasks carry metadata), once per async archive load, with a short timeout and
  fall-through on failure — bounded cost, never on the UI thread, never on the pending path.
