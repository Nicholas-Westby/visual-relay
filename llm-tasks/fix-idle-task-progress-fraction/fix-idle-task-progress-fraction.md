# Task: Make a finished task's progress bar reach full

Every card in the queue/archive column carries a 3 px progress bar. While a task
runs it is driven by a live high-water mark of the stage number reached, which is
honest. The moment the task finishes and the list reloads, the bar switches to a
second, unrelated formula — the number of `stage*-attempt*.report.json` files on
disk, over twelve — and that formula can never reach full.

Across the 115 tasks in this repo's own `.relay/` history, **not one renders a
full bar**. The most common value is 75%, and 75% is what a *perfect* run looks
like: review passed, so Fix and Fix-verify were skipped, so they wrote no report.
The better a run went, the emptier its bar. The archive column therefore reads as
a wall of unfinished work, and a card that says "Completed" sits directly above a
bar that says it is three-quarters done.

Four separate mechanisms push the number around, none of them related to
progress:

1. **The last stage never counts.** Stage 12 (Commit) is a driver stage, not an
   LLM stage. It launches no subagent and writes no report. The ceiling is 11/12
   = 91.7% for every task ever run.
2. **Skipped stages read as unfinished.** Stages 9 (Fix) and 11 (Fix-verify) are
   deliberately skipped when the review pair comes back clean. Skipping is a
   success, but it subtracts from the bar.
3. **A stage that is not in the pipeline is added to the numerator.**
   Visual-triage is a synthetic stage numbered 0, created inline by the driver
   and absent from `RelayStages.All`. Its report is counted, but 0 is not one of
   the twelve, so any task that ran triage gets a free +8.3 points.
4. **The denominator does not match the run.** 29 of the 115 tasks were run under
   the older 11-stage pipeline. They completed every stage they had, and still
   render at most 11/12.

The information needed to render this honestly is already on disk, already
complete, and already read elsewhere in the app: `status.json` records all twelve
stages with a terminal status each. All five cards visible in the report that
prompted this task have `Commit=Done` in that file while rendering between 75%
and 91.7%.

One correction to the record. The sibling task
`llm-tasks/completed/fix-running-task-progress-fraction/` stated that the idle
branch was correct and put it out of scope — "A non-running card showing
`Task.CompletedStageCount / 12` … is correct". That judgment was wrong, and this
task supersedes it. The running branch that task built is correct and must not be
touched.

### Evidence (2026-08-20)

- `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs:108-110` is the whole
  defect:
  `ProgressFraction => IsRunning ? Math.Clamp(_liveCompletedStageCount / (double)RelayStages.All.Count, 0, 1) : Math.Clamp(Task.CompletedStageCount / (double)RelayStages.All.Count, 0, 1)`.
  The bar itself is `src/VisualRelay.App/Views/Controls/TaskCard.axaml:75-86`
  (`Height="3"`, `Minimum="0" Maximum="1"`, `Value="{Binding ProgressFraction}"`).
- The idle numerator is a **file count, not a stage count**.
  `src/VisualRelay.Domain/RunMetrics.cs:45` defines
  `CompletedStageCount => Stages.Count`;
  `src/VisualRelay.Core/Tasks/RelayRunHistory.cs:21-29` builds `Stages` by
  globbing `stage*-attempt*.report.json` and grouping by the number parsed from
  the filename (`:166-167`);
  `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:243-256` (`AttachRunMetrics`)
  copies it onto `RelayTaskItem.CompletedStageCount`.
- Stage 12 writes no report. `src/VisualRelay.Core/Execution/RelayStages.cs:24`
  declares `new(12, "Commit", "cheap", "driver", "none", "git", …)` — runner
  `driver`, not `llm`. Measured across `.relay/`: **0** files matching
  `*/stage12-attempt*.report.json`, against 115 tasks with run history.
- Skipped stages write no report either.
  `src/VisualRelay.Core/Execution/RelayDriver.SkipStages.cs:29` (`FixSkipReason`)
  returns a reason whenever the text review is clean, and `:80`
  (`RecordFixSkipAsync`) records the stage — ledger, seal, `Skipped` status,
  terminal `stage_done` — without launching a subagent, so no report file
  appears.
- Stage 0 is not a pipeline stage.
  `src/VisualRelay.Core/Execution/RelayDriver.ReviewPairTriage.cs:22-24`
  constructs `new RelayStageDefinition(0, "Visual-triage", "cheap", "llm", …)`
  inline. `RelayStages.All` holds numbers 1-12 only, so
  `RelayRunHistory.ReadStageMetric` cannot even name it — the lookup at
  `RelayRunHistory.cs:94` misses and it falls back to the literal string
  `"Stage 0"`. Measured: **71 of 115** tasks carry a `stage0-attempt*.report.json`
  and are inflated by one.
- Measured on the reported screenshot (1508x996), fill `#3191FF` against track
  `#222833`, five bars, each 3 px tall (middle row listed). Every one lands on an
  exact twelfth:
  - y=205 — 215 px fill / 42 px track = 83.65% = **10/12**
  - y=351 — 236 / 21 = 91.82% = **11/12** (`show-stage-tier-on-stage-cards`)
  - y=473 — 215 / 42 = 83.65% = **10/12** (`show-model-host-in-live-tiers`)
  - y=619 — 193 / 64 = 75.09% = **9/12** (`10-guard-tier-tables-against-template`)
  - y=765 — 193 / 64 = 75.09% = **9/12** (`09-strip-repo-local-git-identity-in-pre-commit`)
  The report counts on disk for those four named tasks are 11, 10, 9 and 9. The
  rendered fractions and the file counts agree exactly.
- All five of those tasks are finished. Their `status.json` files each hold 12
  entries ending `11:Fix-verify=Skipped 12:Commit=Done`, with every other stage
  `Done`.
- `status.json` is complete and universal here. Measured across `.relay/`: **115
  of 115** tasks with run history have one; 86 hold 12 entries and 29 hold 11.
  The only statuses that ever appear are `Done` (1120), `Skipped` (197),
  `Waiting` (30) and `Flagged` (4).
- The 29 short records are the older 11-stage pipeline — the missing number is 12
  in all 29, and they read
  `… 7:Review=Done 8:Fix=Done 9:Verify=Done 10:Fix-verify=Done 11:Commit=Done`.
  Every stage they have is `Done`, so they are complete runs and must render
  full.
- The record is already defined and already read by the app.
  `src/VisualRelay.Domain/StageStatus.cs:11-22` is `StageStatusEntry(int Stage,
  string Name, string Status, …)`; `:53-70` is `StageStatusRecord.Read`, which
  returns an empty list when the file is missing or unreadable;
  `src/VisualRelay.Core/Tasks/RelayRunHistory.cs:122-126` already wraps it as
  `ReadStatusRecord(rootPath, taskId)` and its doc comment calls it "the single
  source of truth for stage status".
- Distribution over all 115 tasks, today versus `settled / entries` where settled
  means `Done` or `Skipped`:
  - today: 33.3% x3, 41.7% x1, 66.7% x9, 75.0% x64, 83.3% x28, 91.7% x10.
    **0 tasks at 100%.**
  - proposed: **109 tasks at 100%**, plus 33.3% x1, 36.4% x2, 45.5% x1, 63.6% x1,
    83.3% x1.
  - The six that stay short are exactly the six runs that really did stop early —
    the four carrying a `Flagged` stage (`allow-tasks-to-skip-automated-testing`
    7/11, `show-cost-per-llm-model` 10/12, `speed-up-headless-ui-tests` 4/12,
    `upgrade-nono` 5/11) plus `make-stage-retries-always-escalate` and
    `skip-fix-stages-on-clean-review`, both abandoned after stage 4 with every
    later stage still `Waiting`.
- The bar visibly **goes backwards at the moment of completion** today, which is
  the same defect seen live. The driver publishes `stage_done` for every stage
  including the skipped and driver ones — from
  `.relay/show-model-host-in-live-tiers/run.log`:
  `s11/balanced stage_done name=Fix-verify time=0s … status=Skipped` then
  `s12/cheap stage_done name=Commit time=1s … status=Done`.
  `MainWindowViewModel.Helpers.cs:90` routes that into `CompleteRunningStage`
  (`MainWindowViewModel.LiveState.cs:171-189`) →
  `RecordLiveCompletedStage` (`MainWindowViewModel.LiveProgress.cs:31-37`), a
  `Math.Max` high-water mark. So that card reaches **12/12 = 100% while running**
  and drops to **10/12 = 83.3%** as soon as it is archived and reloaded.
- Other consumers of `RelayTaskItem.CompletedStageCount`, all of which must keep
  working unchanged:
  - `src/VisualRelay.Domain/RelayTaskItem.cs:19-21` — the `== 0` gates behind
    "No cost yet" / "No run yet" / "No run history".
  - `src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs:178` —
    `CanRewriteSelected` refuses to rewrite a task whose `CompletedStageCount != 0`,
    i.e. uses it as "has this ever run".
  - `src/VisualRelay.Domain/RunMetrics.cs:52-54` — `SummaryLabel`, surfaced as
    `SelectedTaskMetricLabel` at
    `src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs:23`. This is
    the "10 stages" chip in the task pane. It is inflated by triage in the same
    way, and it is out of scope — see below.
- Existing tests that pin the idle branch. Under the fallback described below,
  **all of them stay green without edits**; if a change makes one fail, the
  change is wrong.
  - `tests/VisualRelay.Tests/TaskRowViewModelTests.cs:20-23`
    `ProgressFraction_IsZeroWithNoRunHistory`.
  - `:26-32` `ProgressFraction_ScalesWithCompletedStageCount` — 12 → 1.0, 5 →
    5/12, 99 → 1.0 (the clamp).
  - `:206-212` `ProgressFraction_UsesRelayStagesDenominator`.
  - `:230-240` `ProgressFraction_FallsBackToRunMetricsWhenIdle` — 6 stages reads
    6/12 idle, 3/12 while running, 6/12 again after `MarkIdle`.
  - `:243-…` `UpdateTask_NotifiesProgressFraction`.
  - The rows in all of these come from `NewTask` at `:298-300`, which sets only
    `CompletedStageCount` and no status record.
  - `tests/VisualRelay.Tests/DesignDataTests.cs:58` asserts
    `DesignData.Card.ProgressFraction` is inside `(0.01, 0.99)`;
    `tests/VisualRelay.Tests/QueuePanelSplitRenderTests.cs:44` asserts the
    rendered `ProgressBar.Value` equals it. `DesignData.Card` is a **running**
    card (`src/VisualRelay.App/DesignTime/DesignData.cs:21-28`:
    `MarkRunning(9, "Fix")` plus `RecordStageCompleted(1..8)` → 8/12), so the
    live branch serves it and the idle change cannot move it.

### What to build

Drive the idle branch of `ProgressFraction` from the stage status record instead
of from the count of report files, so a finished run reads full.

- Add a small reader beside the existing one in
  `src/VisualRelay.Core/Tasks/RelayRunHistory.cs` (168 lines, plenty of room)
  that returns the settled-stage progress for a task: read
  `StageStatusRecord.Read` (or reuse `ReadStatusRecord` at `:122-126`), count the
  entries whose `Status` is `Done` or `Skipped`, and return that alongside the
  record's own entry count.
  - **Settled means `Done` or `Skipped` only.** `Waiting`, `Running` and
    `Flagged` are not progress — a flagged stage is where the run stopped, and
    the card already signals that separately in amber.
  - **The denominator is the record's own entry count** when it has entries,
    falling back to `RelayStages.All.Count`. This is what makes the 29 legacy
    11-stage runs read 11/11 rather than 11/12, and it keeps the bar meaningful
    if the pipeline changes length again.
  - Missing, empty or unparseable record → report zero entries and let the caller
    fall back. `StageStatusRecord.Read` already returns `[]` rather than throwing
    for a missing or unreadable file.
- Carry it on `RelayTaskItem` (`src/VisualRelay.Domain/RelayTaskItem.cs`, 34
  lines) as two new positional parameters — settled count and pipeline stage
  count — both defaulting to `0`, **appended after `CompletedAt`**. Do not insert
  them mid-record: `DesignData.cs:55-56` constructs `RelayTaskItem` with eleven
  positional arguments and would silently bind the wrong values.
- Populate them in `AttachRunMetrics`
  (`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:243-256`). That method
  already runs for every task on both list paths (`:38-39` and `:127`), and the
  repository already reads per-task files out of `.relay/{id}` in
  `AttachReviewState` (`:231-241`), so this adds one file read per task on a path
  that already does one directory enumeration per task. Keep the edit to a couple
  of lines — see the headroom note in Constraints.
- Change only the **idle** arm of `ProgressFraction`
  (`TaskRowViewModel.cs:108-110`): when the pipeline stage count is greater than
  zero use `settled / pipelineStages`, otherwise keep today's
  `Task.CompletedStageCount / RelayStages.All.Count`. Keep the `Math.Clamp(…, 0, 1)`.
  - That fallback is not test scaffolding — it is the real behaviour for a task
    with no status record on disk — but it is also what keeps every existing
    idle-branch test, `DesignData` and the screenshot harness working untouched,
    since none of them writes a status record.
  - `UpdateTask` (`TaskRowViewModel.cs:44-53`) already raises `ProgressFraction`;
    the new fields ride along on the same record swap, so no extra notification
    plumbing is needed.
- Tests.
  1. Reader level: a 12-entry record with the real shape from this repo —
     `1..10 Done`, `11 Skipped`, `12 Done` — settles 12 of 12. An 11-entry
     all-`Done` record settles 11 of 11. A record ending
     `6 Flagged, 7..12 Waiting` settles 5 of 12. A missing `status.json` yields
     zero entries.
  2. Row level: a row whose item carries settled 12 of 12 reads `1.0` **even
     though its `CompletedStageCount` is 9** — that single assertion is the whole
     bug. A row carrying settled 11 of 11 reads `1.0`. A row with a zero pipeline
     count still reads `CompletedStageCount / 12`, and a row with no run history
     still reads `0.0`.
  3. Repository level: build a temp root, write a `.relay/{id}/status.json` and a
     couple of report files that deliberately disagree with it, and assert the
     `RelayTaskItem` comes back with the settled counts from the record and its
     `CompletedStageCount` still from the reports. `MarkIdle` must still fall back
     to the idle branch, not to zero.
  4. The live branch must be provably untouched: a running row seeded to 6 still
     reads 6/12 regardless of what the settled counts say, and still falls back to
     the idle value on `MarkIdle`.
- Visual confirmation is secondary and must not gate the task. The bar is 3 px
  tall; the check is a fill-length measurement, not an eyeball. The screenshot
  harness seeds no status records, so the standard render is expected to be
  **pixel-identical** — that is the desired outcome there, not a failure.

### Out of scope

- **Do not touch the running branch** of `ProgressFraction`, the
  `_liveCompletedStageCounts` dictionary
  (`MainWindowViewModel.LiveProgress.cs`), `RecordStageCompleted`,
  `SeedCompletedStageCount`, `CompleteRunningStage` or `ApplyRunningTaskToRows`.
  The live high-water mark is correct and reaches 12/12 at Commit; this task
  makes the idle branch agree with it, not the other way round.
- Do not change what `TaskRunMetric.CompletedStageCount` or
  `TaskRunMetric.Stages` mean, and do not filter stage 0 out of them. That count
  is a *metrics* concept — it backs cost and duration totals and the
  "has this ever run" gate at `MainWindowViewModel.Rewrite.cs:178`. Excluding
  triage there would also silently drop triage's cost out of the archive's money
  figures.
- Leave the "N stages" chip (`RunMetrics.cs:52-54` via
  `MainWindowViewModel.RunHistory.cs:23`) exactly as it is. It counts triage as a
  stage and is a real but separate defect; it needs its own task because the fix
  touches cost reporting.
- Do not change the bar's geometry, height, colours, corner radius, margin or
  `AccentBrush` wiring in `TaskCard.axaml`, and do not add a percentage label,
  tooltip, second indicator or per-stage segmentation.
- Do not touch anything under `src/VisualRelay.Core/Execution/`. The driver
  writes the status record correctly and completely; only the GUI's reading of it
  is wrong. In particular do not make stage 12 write a report, do not renumber
  Visual-triage, and do not change what a skipped stage records.
- Do not change `StageStatusEntry`, `StageStatusRecord`, `status.json`'s on-disk
  shape, or the Control API surface.
- Do not modify `tools/VisualRelay.Screenshots/Program.cs` or the seeded values in
  `src/VisualRelay.App/DesignTime/DesignData.cs`. The fallback is designed so
  neither needs an edit.

### Constraints

- Hard guard: no `.cs`/`.axaml` under `src/`, `tests/` or `tools/` may exceed 300
  lines. `tools/VisualRelay.Guards/FileSizeGuard.cs:13` sets the limit and
  `tools/VisualRelay.Cli/Gates/GuardRunner.cs:32-38` runs it over
  `["src", "tests", "tools"]`, failing on `lines > limit`. Every file below ends
  with a newline, so `wc -l` matches the guard exactly.
- Measured counts and headroom for the files this change touches:
  - `src/VisualRelay.Domain/RelayTaskItem.cs` — **34** (266 spare).
  - `src/VisualRelay.Domain/StageStatus.cs` — **71** (229 spare). No edit needed.
  - `src/VisualRelay.Core/Tasks/RelayRunHistory.cs` — **168** (132 spare). The
    comfortable home for the new reader.
  - `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs` — **292**, i.e. **8
    spare**. Very tight. Keep `AttachRunMetrics` to a one-line read plus the two
    added initialisers; put every helper, doc comment and constant in
    `RelayRunHistory.cs` instead. If it would still land over 300, move a whole
    existing method out to a new partial rather than compressing comments.
  - `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs` — **218** (82 spare).
  - `tests/VisualRelay.Tests/TaskRowViewModelTests.cs` — **300**, i.e. **exactly
    at the limit, zero headroom**. Adding one line fails the build. It is already
    `public sealed partial class` at `:9` and already has a part file,
    `tests/VisualRelay.Tests/TaskRowViewModelTests.LiveProgress.cs` (**65**
    lines) — put new row tests there or in a new part.
  - `tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs` — **300**, also at the
    limit, and declared `public sealed class` at `:5` — **not** partial. Either
    change `:5` to `public sealed partial class` (no line growth) and add a part
    file, or start a separate test class in its own file.
  - `tests/VisualRelay.Tests/RelayRunHistoryTests.cs` — **231** (69 spare). Good
    home for the reader tests.
  - `src/VisualRelay.App/Views/Controls/TaskCard.axaml` — **92**. No edit needed.
- `RelayTaskItem` is a positional record. New parameters go **after**
  `CompletedAt`, with defaults, or the eleven-argument positional call at
  `src/VisualRelay.App/DesignTime/DesignData.cs:55-56` binds them wrongly without
  a compile error. `tools/VisualRelay.Screenshots/Program.cs:103-104` uses named
  arguments and is safe either way.
- `AttachRunMetrics` runs once per task on every list load, for 120+ tasks. Read
  `status.json` at most once per task, and do not read trace directories, ledger
  or seals.
- Protected paths `llm-tasks/`, `.relay/` and `.swival/` must never appear in this
  task's diff. Send any throwaway probe or scratch artifact to `.relay/scratch/`.
