# Task: Keep a running task's progress bar honest across list reloads and resumes

The progress bar on a queue card is the only at-a-glance signal of how far a run
has got. While a task runs, `ProgressFraction` reads a counter that lives on the
task row object and is bumped once per `stage_done`. Every path that reloads the
task list throws those row objects away and builds new ones, so the counter
silently resets to zero in the middle of a run: the bar snaps back to empty and
stays wrong for every remaining stage, because the counter only ever counts
forward from wherever it was left. Refresh, Toggle Archive, Follow-running-task
and Save-edit are all reachable while a drain is in flight, and the Refresh path
was deliberately made busy-tolerant, so this is not a corner case.

The same counter also starts at zero on a **resumed** run. The driver picks up at
`firstStageToRun` and never re-emits `stage_done` for the stages that already
completed, so a task resumed at stage 10 shows 0% and finishes the run at 3/12
(25%) even though all twelve stages are done. Both symptoms come from the same
root cause: the live value is stored in the one place that does not survive a
reload, and it is a bare increment that carries no information about which stage
it belongs to.

One thing here is **not** a defect and must not be "fixed". When a row is idle,
`ProgressFraction` falls back to `Task.CompletedStageCount`, which is the number
of stages the task's *last* run recorded. A Pending card with a full bar is
therefore telling the truth — it ran all twelve stages before. In the checked-in
renders the full bars are purely a screenshot-harness seeding artifact, not
evidence of anything.

### Evidence (2026-08-20)

- `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs:108-110` defines
  `ProgressFraction` as `IsRunning ? _liveCompletedStageCount / 12 :
  Task.CompletedStageCount / 12` (`RelayStages.All.Count` is 12 —
  `src/VisualRelay.Core/Execution/RelayStages.cs:7-25`).
- `_liveCompletedStageCount` is declared at `TaskRowViewModel.cs:29`. It is
  written in exactly two places: `RecordStageCompleted` (`:174-178`), which does
  `_liveCompletedStageCount++` and **ignores its `stageNumber` argument
  entirely**, and `MarkIdle` (`:182`), which zeroes it. Nothing seeds it.
- `ReloadTaskListAsync` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:139-184`)
  calls `Tasks.Clear()` at `:163`, constructs a brand-new `TaskRowViewModel` per
  task at `:173`, then `ApplyRunningTaskToRows()` at `:179`. The new rows start
  with `_liveCompletedStageCount == 0`.
- `ApplyRunningTaskToRows` (`MainWindowViewModel.LiveState.cs:239-254`) only
  calls `MarkRunning(stageNum, stageName, stageNums)`. It restores the running
  flag and the stage label but nothing about completed stages — so after a
  mid-run reload the card still reads e.g. "Stage 09 · Fix" beside an empty bar.
- Production paths that reach `ReloadTaskListAsync` during a live run:
  - `MainWindowViewModel.Commands.cs:23-48` — `RefreshAsync` has an explicit
    `if (IsBusy)` branch commented "Mid-drain: reload directly", and
    `CanRefresh()` is just `Directory.Exists(RootPath)`
    (`MainWindowViewModel.Helpers.cs:231`) with no busy gate.
  - `MainWindowViewModel.Commands.cs:111-120` — `ToggleArchiveAsync`, whose own
    comment reads "When a drain is active…".
  - `MainWindowViewModel.Commands.cs:90-109` — `FollowRunningTaskAsync`, the
    jump-to-the-running-task action.
  - `MainWindowViewModel.Authoring.cs:103` — `SaveEditAsync`, reachable for any
    task other than the running one.
  - `src/VisualRelay.App/Services/ControlApi.cs:38` maps `"refresh"` onto
    `RefreshCommand`, and `:224-226` executes it.
- Resume path: `MainWindowViewModel.Execution.cs:38-56` (`ResumeSelectedAsync`)
  → `MainWindowViewModel.RunOne.cs:12` `RunOneAsync(task, resume: true)`, which
  calls `BeginRunningTask(task)` unconditionally at `:19` and constructs
  `new RelayDriverOptions(CreateGitCommit: true, Resume: resume)` at `:27`.
  `RelayDriver.cs:51` then `LoadResumeState(...)` advances `firstStageToRun`
  (`RelayDriver.Resume.cs:19-23`); prior stages are not replayed as events, so
  the GUI counter never sees them.
- `RestoreRunningTaskState` (`MainWindowViewModel.LiveState.cs:103-119`) has **no
  production caller**. A repo-wide grep finds it only in `tests/` and in
  `tools/VisualRelay.Screenshots/Program.cs:113`. It is the "adopt a run already
  in flight" hook, and it likewise seeds nothing.
- Measured on the real 1440x900 renders. `.relay/colour-run-log-attention-rows/visual-review/main.png`
  and `.relay/show-stage-tier-on-stage-cards/visual-review/main.png` are
  pixel-identical in the card column. Bar rows (each 3 px tall, y listed for the
  middle row):
  - y=266, x=53–301 (249 px): uniformly `#222833`, the empty track — the
    running+selected `add-multiply-helper` card, whose text reads
    "Running / Stage 03 · Diagnose". **0% while at stage 3.**
  - y=417, x=52–302 (251 px): uniformly `#3191FF` — Pending
    `fix-csv-export-encoding`, 100%.
  - y=542, x=51–303 (253 px): uniformly `#222833` — the running
    `rate-limit-middleware` card ("Running / Starting task"), 0%.
  - y=665, x=50–304 (255 px): uniformly `#3191FF` — Pending
    `stabilise-flaky-retry-test`, 100%.
  - y=787: `#F2C66D` for x=50–90 (**41 px**) then `#222833` for x=92–304 — the
    needs-review `extract-theme-tokens` card at 2/12 = 16.7%.
- Those renders are produced by this repo's own harness: `.relay/config.json`
  sets `"visualRenderCmd": "dotnet run --project tools/VisualRelay.Screenshots -- {outDir}/main.png"`,
  and `src/VisualRelay.Core/Execution/RelayDriver.ReviewPairTriage.cs:62-64`
  writes into `<taskDirectory>/visual-review/`. Default size is 1440x900
  (`tools/VisualRelay.Screenshots/Program.cs:13-14`).
- The two full bars are seeding, not a bug: `tools/VisualRelay.Screenshots/Program.cs:106`
  and `:108` build those demo cards with `stages: 12`, i.e. `CompletedStageCount:
  12`, and they never run. `:109` seeds `stages: 2`, which is the 41 px amber
  bar. `:104` seeds the running card with `CompletedStageCount: 12` as well, but
  it is `IsRunning`, so the live branch wins and renders 0.
- `tools/VisualRelay.Screenshots/Program.cs:123` puts the third card into the
  running state with a bare `Tasks[2].MarkRunning()` — no view-model bookkeeping
  at all. That card's empty bar is expected and will stay empty after this fix.
- `src/VisualRelay.App/Views/Controls/TaskCard.axaml:75-86` is the bar:
  `Height="3" MinHeight="3"`, `Minimum="0" Maximum="1"`,
  `Value="{Binding ProgressFraction}"`, `Background="#222833"`,
  `Foreground="{Binding AccentBrush}"`.
- Existing tests that pin today's behaviour, all of which must keep passing
  unchanged:
  - `tests/VisualRelay.Tests/TaskRowViewModelTests.cs:214-227`
    `ProgressFraction_UsesLiveCountWhenRunning` — `MarkRunning()` on a row whose
    record has 0 stages gives 0.0; twelve `RecordStageCompleted(1..12)` calls
    give 1.0 and **exactly twelve** `ProgressFraction` notifications.
  - `tests/VisualRelay.Tests/TaskRowViewModelTests.cs:229-240`
    `ProgressFraction_FallsBackToRunMetricsWhenIdle` — a row whose record has 6
    stages reads 6/12 idle, then after `MarkRunning()` and
    `RecordStageCompleted(1,2,3)` reads **3/12, not 9/12 and not 6/12**, then
    6/12 again after `MarkIdle()`. This is the test that forbids seeding the
    live count from `Task.CompletedStageCount` on a fresh run start.
  - `tests/VisualRelay.Tests/MainWindowViewModelTests.Status.cs:231-254`
    `LiveProgressFraction_IncrementsOnStageDone` — `RestoreRunningTaskState(id,
    1, "Ideate")` then 0.0, then six dispatched `stage_done` events for stages
    1–6 give 6/12 and **exactly six** notifications.
  - `tests/VisualRelay.Tests/DesignDataTests.cs:58` asserts
    `DesignData.Card.ProgressFraction` is within `(0.01, 0.99)`;
    `src/VisualRelay.App/DesignTime/DesignData.cs:21-27` builds it with
    `MarkRunning(9, "Fix")` and `RecordStageCompleted(1..8)` → 8/12.
  - `tests/VisualRelay.Tests/QueuePanelSplitRenderTests.cs:44` asserts the
    rendered `ProgressBar.Value` equals `DesignData.Card.ProgressFraction`.

### What to build

Make a running task's progress bar survive a task-list reload, and make it
reflect the true stage reached on a resumed run.

- Move the authoritative live value off the row and onto `MainWindowViewModel`,
  keyed by task id, so it outlives `Tasks.Clear()`. A
  `Dictionary<string, int>` of "highest stage number completed in this session"
  is enough. Put the dictionary and its small helpers in a **new**
  `MainWindowViewModel` partial (the class is already split across 34 files; name it
  something like `src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveProgress.cs`)
  rather than in `MainWindowViewModel.cs` or `MainWindowViewModel.LiveState.cs`,
  both of which are nearly full — see Constraints.
- Wire it at the five existing seams in `MainWindowViewModel.LiveState.cs`,
  one call-site line each:
  - `BeginRunningTask` (`:121-138`) — set the entry to `0`. A fresh run genuinely
    starts at zero; this is what keeps `ProgressFraction_FallsBackToRunMetricsWhenIdle`
    green.
  - `CompleteRunningStage` (`:169-187`) — raise the entry to
    `Math.Max(existing, stageNumber)` and push the new value into the row.
  - `ClearRunningTask` (`:189-205`) — remove the entry.
  - `ApplyRunningTaskToRows` (`:239-254`) — after `MarkRunning(...)`, push the
    stored value into the (possibly brand-new) row. This is the line that fixes
    the reload regression.
  - `RestoreRunningTaskState` (`:103-119`) — set the entry to
    `Math.Max(0, stageNumber - 1)` before `ApplyRunningTaskToRows()`. If stage N
    is running, stages 1…N-1 are done; a null `stageNumber` means 0.
- On `TaskRowViewModel`, add an `internal` seeding method (e.g.
  `SeedCompletedStageCount(int)`) that assigns `_liveCompletedStageCount` and
  raises `ProgressFraction`, and change `RecordStageCompleted(int stageNumber)`
  to use its argument as a high-water mark —
  `_liveCompletedStageCount = Math.Max(_liveCompletedStageCount, stageNumber)` —
  instead of a blind `++`. That is what makes a resumed run read 10/12 when
  stage 10 completes, and it removes the currently-unused parameter smell.
- **Keep the `OnPropertyChanged(nameof(ProgressFraction))` in
  `RecordStageCompleted` unconditional.** Two existing tests count notifications
  (twelve at `TaskRowViewModelTests.cs:226`, six at
  `MainWindowViewModelTests.Status.cs:253`); making the notification conditional
  on the value changing breaks both.
- Stages 7 (Review) and 8 (Visual-review) run in parallel
  (`RelayStages.cs:19-20`). Under a high-water mark, if 8 finishes before 7 the
  bar reads 8/12 while 7 is still running. That is accepted and intended — do not
  add special-casing for the pair.
- Tests. This defect is worth more test coverage than screenshot coverage, so
  lead with these:
  1. Row level: a running row seeded to N reads N/12; `RecordStageCompleted` with
     a *lower* stage number does not move it backwards; `RecordStageCompleted`
     with the same stage number twice does not double-count; `MarkIdle` still
     drops back to the record's `CompletedStageCount`.
  2. Reload regression (the headline): build a view-model over a temp repo, put a
     task into the running state, dispatch `stage_done` for stages 1–6, assert
     6/12, then run the reload path that production uses — `await
     vm.RefreshCommand.ExecuteAsync(null)` with `IsBusy = true` — and assert the
     row for that task is **still** 6/12 rather than 0. Re-fetch the row from
     `vm.Tasks` after the refresh, because the old instance is discarded.
     `tests/VisualRelay.Tests/RefreshButtonDuringRunTests.cs` is already
     `sealed partial` and has the fixture pattern for this (`:59-91`).
  3. Resume-shaped: with the task running and no prior live events, dispatch a
     single `stage_done` for stage 10 and assert 10/12 rather than 1/12.
  4. `RestoreRunningTaskState(id, 3, name)` seeds 2/12;
     `RestoreRunningTaskState(id, 1, name)` seeds 0.0 (this is the existing
     assertion at `MainWindowViewModelTests.Status.cs:246` and it must stay
     green); `RestoreRunningTaskState(id, null, null)` seeds 0.0.
  `tests/VisualRelay.Tests/RelayEventTestDispatch.cs` already provides
  `Dispatch`, `StageStart`, `StageDone` and `Flagged` builders — use them rather
  than adding a new reflection helper.
- Visual confirmation, secondary. The bar is 3 px tall, so a human comparing
  screenshots will not reliably see this; the check is a **fill-length**
  measurement, not an eyeball. With no harness change,
  `tools/VisualRelay.Screenshots/Program.cs:113` seeds stage 3, so the
  `add-multiply-helper` card's bar at y=265–267 must go from 249 px of uniform
  `#222833` to roughly **41 px of `#5AD47D` starting at x=53**, then track. The
  41 px figure is not a guess — it is the measured width of the 2/12 amber fill
  on the `extract-theme-tokens` card at y=787 in the current renders, over a
  track of the same width. The `rate-limit-middleware` bar at y=541–543 stays
  fully empty (it is driven by a bare `MarkRunning()` with no view-model state);
  do not treat that as a failure. Do not gate the task on this.

### Out of scope

- Do not change the idle branch of `ProgressFraction`. A non-running card showing
  `Task.CompletedStageCount / 12` — including a Pending card at 100% — is
  correct and is pinned by `ProgressFraction_FallsBackToRunMetricsWhenIdle`.
- Do not change the bar's geometry, colours, corner radius, margin or
  `AccentBrush` wiring in `TaskCard.axaml`. Making a 3 px bar easier to see is a
  different task.
- Do not modify `tools/VisualRelay.Screenshots/Program.cs`. The fix must show up
  in the standard render with the harness untouched.
- Do not persist live progress to disk, and do not read `StageStatusRecord` or
  `RelayRunHistory` to reconstruct it. The view-model already knows enough.
- Do not touch `RelayDriver`, `RelayDriverOptions`, `LoadResumeState` or anything
  under `src/VisualRelay.Core/Execution/`. The driver's resume behaviour is
  correct; only the GUI's mirror of it is wrong.
- Do not change the Control API surface, `RelayTaskItem`, `TaskRunMetric`, or
  `RelayStages`.
- Do not add a second progress indicator, a percentage label, or a tooltip.
- Do not rework `_runningStageNumbers` / `_runningStageNames` or the
  multi-task concurrency model in `MainWindowViewModel.LiveState.cs`.

### Constraints

- Hard guard: no `.cs`/`.axaml` under `src/`, `tests/` or `tools/` may exceed 300
  lines. `tools/VisualRelay.Guards/FileSizeGuard.cs:13` sets the default limit to
  300, `tools/VisualRelay.Cli/Gates/GuardRunner.cs:34` runs it over
  `["src", "tests", "tools"]`, and it fails on `lines > limit`. Every file below
  ends with a newline, so `wc -l` equals the guard's count exactly.
- Measured counts and headroom for the files this change touches:
  - `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs` — **212** (88 spare).
    The comfortable place to work.
  - `src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs` — **293**
    (**7 spare**). Very tight. The five seams above cost about four added lines
    if each is a single call. Add no comment blocks here, and put every new
    field, helper and doc-comment in the new partial instead. If it would still
    land over 300, move a whole method out to the new partial rather than
    compressing or deleting existing comments.
  - `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` — **296** (4 spare).
    Prefer not to touch it at all; the new dictionary belongs in the new partial.
  - `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs` — **298**
    (**2 spare**). Do not add anything here. `ReloadTaskListAsync` needs no edit:
    it already calls `ApplyRunningTaskToRows()` at `:179`, which is where the
    restore hooks in.
  - `tests/VisualRelay.Tests/TaskRowViewModelTests.cs` — **300**, i.e. **exactly
    at the limit, zero headroom**, and it is declared `public sealed class` at
    `:9`. Adding a single line fails the build. Change `:9` to
    `public sealed partial class` (same line count, no growth) and put new row
    tests in a new part file, or start a separate test class in its own file.
  - `tests/VisualRelay.Tests/RefreshButtonDuringRunTests.cs` — **180** (120
    spare), already `sealed partial` at `:16`. Best home for the reload
    regression test.
  - `tests/VisualRelay.Tests/MainWindowViewModelTests.Status.cs` — **255** (45
    spare).
  - `tests/VisualRelay.Tests/LiveStateViewModelTests.cs` — **280** (20 spare).
  - `src/VisualRelay.App/Views/Controls/TaskCard.axaml` — **92**. No edit needed.
- `MainWindowViewModel` partials carry a
  `// ReSharper disable once UnusedType.Global — partial of MainWindowViewModel`
  comment above the class (see `MainWindowViewModel.RunOne.cs:9`). Match that in
  the new file.
- `ProgressFraction` keeps its `Math.Clamp(..., 0, 1)`, so a high-water mark
  above 12 can never render past full.
