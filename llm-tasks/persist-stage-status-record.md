# Persist an authoritative per-stage status record the UI reads directly

Per-stage status in Visual Relay is **reconstructed from scratch** every time the
archived/run-history view opens, by re-parsing each swival `report.json`. That
reconstruction is wrong in two ways: it misreads every completed stage as failed
(because it checks for `outcome == "ok"` but swival emits `outcome == "success"`),
and the driver-only Commit stage emits no report at all so it never gets a status.
The result: a task that actually **succeeded and committed** shows all of its
stages "Flagged", with Commit stuck on "Waiting / No run yet". The fix is
structural — have the driver write an authoritative per-stage status record as the
run progresses, and have the UI read that record as the single source of truth for
stage status instead of inferring it from report outcomes.

## Current state (researched)

- **Status is derived from `report.json`, and the success test is wrong.**
  `RelayRunHistory.ReadResult` (`src/VisualRelay.Core/Tasks/RelayRunHistory.cs:117-155`)
  opens each `stage*-attempt*.report.json`, reads `result.outcome`, and returns
  `(false, errorMessage)` whenever `outcome != "ok"`
  (`RelayRunHistory.cs:135-140`). swival actually emits `result.outcome ==
  "success"` (with `exit_code == 0`) for a passing stage, so the `!= "ok"`
  comparison treats **every** completed stage as failed. `ReadStageMetric`
  (`RelayRunHistory.cs:81-115`) threads that `succeeded` flag into the
  `StageRunMetric`, and `StageRowViewModel.ApplyMetric`
  (`src/VisualRelay.App/ViewModels/StageRowViewModel.cs:135-147`) does
  `Status = metric.Succeeded ? "Done" : "Flagged"` — so a fully successful task
  renders every stage card as **Flagged**.
- **The Commit stage has no report, so it gets no metric and no status.** Stage 11
  is the driver-only `Commit` stage (`RelayStages.cs:19`, `Kind == "driver"`).
  In the stage loop (`src/VisualRelay.Core/Execution/RelayDriver.cs:63-65`) it just
  sets a ledger `body` and never calls swival, so no `stage11-attempt*.report.json`
  is written. `ReadTaskMetric` only enumerates `stage*-attempt*.report.json`
  (`RelayRunHistory.cs:20`), so stage 11 produces **no** `StageRunMetric`. In the
  archived view `LoadRunHistoryAsync`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs:20-28`) finds
  no metric for stage 11, so its card stays at the constructor default
  `"Waiting"` (`StageRowViewModel.cs:27`) showing "Waiting / No run yet" even
  though the task committed.
- **Status is set in three different places today, none authoritative.**
  - Live, from driver events: `ApplyStageEventToBoard`
    (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:64-88`) maps
    `stage_start → "Running"`, `stage_done`/`stage_report → "Done"`,
    `flagged → "Flagged"`. The driver publishes these via `PublishAsync`
    (`stage_start`, `RelayDriver.cs:57,241-242`), `PublishStageDoneAsync`
    (`stage_done`, `RelayDriver.cs:171,244-283`), and `FlagAsync` (`flagged`,
    `RelayDriver.cs:225-239`). Live status is correct because it uses the event
    stream, **not** report parsing.
  - Archived, from reports: `LoadRunHistoryAsync` → `ReadTaskMetric` →
    `ApplyMetric` (the buggy path above).
  - Replayed events: `ReadTaskEvents` (`RelayRunHistory.cs:31-46`) synthesizes
    `stage_report` events from the report-derived metrics, also fed through
    `ApplyStageEventToBoard`.
- **There is already a per-stage proof file, but it has no status.** The driver
  writes a JSON-lines hash chain to `.relay/<task>/<task>.seals` via
  `WriteArtifactsAsync` (`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs:18-30`),
  one `SerializeSeal` entry per stage (`RelayDriver.Artifacts.cs:117-134`) with
  `kind/n/ts/artifactHash/treeHash/seal` and an optional `check` ("green"/"red"
  for stages 5 and 9). It records that a stage ran, but **not** whether it
  succeeded, and the seal is appended *after* the swival call so a stage that
  flags mid-way (via `FlagAsync`, which returns early) never gets a seal line.
- **Metrics are report-derived and currently correct — don't regress them.**
  Time/cost/turns/model come from `RelayCostEstimator.EstimateReport` through
  `ReadStageMetric` (`RelayRunHistory.cs:91-113`) into `StageRunMetric`
  (`src/VisualRelay.Domain/RunMetrics.cs:3-37`, incl. `Turns`, `Succeeded`),
  rendered by `ApplyMetric` (`StageRowViewModel.cs:135-147`) and live by
  `ApplyStageEventMetric` (`Helpers.cs:255-280`). Only the **success/failure**
  determination is broken; the numeric metrics are fine.
- **`.relay/*` is gitignored except force-committed proof files.** `.gitignore`
  has `.relay/*` with only `!.relay/config.json` un-ignored
  (`.gitignore:14-15`). The committed proof set is force-added by `GitCommitter`
  (`add -f -- <proofFiles>`, `GitCommitter.cs:60-62`); the driver builds that
  list as `ledger.md`, `<task>.seals`, `manifest.txt`
  (`RelayDriver.cs:177`). Report JSONs are **not** committed — so today's
  report-derived metrics are local-only and vanish on a fresh checkout, while the
  proof files survive.

## What to build

One committed direction: **the driver writes a per-stage status record as the run
progresses, and the UI reads it as the single source of truth for stage status.**
Write the failing tests first, then implement.

1. **Define and write the record (driver).** The driver writes
   `.relay/<task>/status.json` — a JSON array of entries, one per stage, shaped:
   `{ stage: int, name: string, status: "waiting"|"running"|"done"|"flagged",
   check?: "green"|"red", durationSeconds?, costUsd?, turns?, model?, error? }`.
   Write/update it transactionally as the run moves:
   - At `stage_start` (`RelayDriver.cs:57`), mark that stage `running` (and seed
     not-yet-reached stages as `waiting` so a flagged run still lists all 11).
   - At `stage_done` (`RelayDriver.cs:171`, where `PublishStageDoneAsync` already
     has `elapsed`, `cost`, `check`), mark it `done` and record the metrics +
     `check`.
   - In `FlagAsync` (`RelayDriver.cs:225-239`), mark the flagged stage `flagged`
     with its `error`, leaving earlier stages `done` and later stages `waiting`.
   - **Crucially, record the driver Commit stage (11) too** — it has no
     `report.json`, so the record is the only place its `done`/`flagged` status
     lives. Mark it `done` after a successful `GitCommitter.CommitAsync`
     (`RelayDriver.cs:179-187`) and `flagged` on commit failure
     (`RelayDriver.cs:181-183`). A zero-cost driver stage records `costUsd: 0`,
     no `turns`, no `model` — mirroring how `PublishStageDoneAsync` already
     suppresses those for driver stages.
   Add the writer next to the existing artifact writers in
   `RelayDriver.Artifacts.cs` (alongside `WriteArtifactsAsync` /
   `WriteManifestAsync`), and a small record/serializer type for the entries.
2. **Make the status record a committed proof file (recommended).** Add
   `.relay/<task>/status.json` to the force-committed proof set in
   `RelayDriver.cs:177` (the `proofFiles` array passed to
   `GitCommitter.CommitAsync`), so archived status survives a fresh checkout —
   consistent with `ledger.md`/`*.seals`/`manifest.txt` and intentionally unlike
   the local-only report-derived metrics. (State this choice explicitly in the
   record's doc comment: committed proof, not local run state.)
3. **Read the record as the source of truth (history).** In `RelayRunHistory`,
   add a reader for `.relay/<task>/status.json`. Replace the
   `result.outcome`-based status determination: **delete or stop calling
   `ReadResult`** (`RelayRunHistory.cs:117-155`) for status purposes so the
   `"success" != "ok"` misclassification is gone *by construction*. The status
   (`waiting`/`running`/`done`/`flagged`) and the per-stage `error` come from the
   record. Metrics (time/cost/turns/model) **stay report-derived** as they are
   today via `EstimateReport` — do not regress them; only the status/error source
   changes. For the Commit stage, which has no report, the record supplies its
   status (and its `durationSeconds`/`costUsd` if you choose to surface them).
4. **Feed the UI from the record (consumers).**
   - Archived view: `LoadRunHistoryAsync`
     (`MainWindowViewModel.RunHistory.cs:7-43`) sets each `StageRowViewModel`'s
     `Status` from the record entry, and `SelectedTaskError` from the
     flagged entry's `error` (replacing the `!stage.Succeeded` scan at
     `RunHistory.cs:14-19`). `StageRowViewModel.ApplyMetric`
     (`StageRowViewModel.cs:135-147`) should no longer be the thing that decides
     `"Done"` vs `"Flagged"` — keep `ApplyMetric` for the numeric labels, but
     drive `Status` from the record. (`StageRunMetric.Succeeded` becomes
     redundant for status; remove it or stop using it for status — don't leave a
     second, conflicting status source.)
   - Live view: keep the event-driven path (`ApplyStageEventToBoard`,
     `Helpers.cs:64-88`) — it is already correct. Ensure the live and reloaded
     status agree (after a run, `LoadRunHistoryAsync` runs at
     `MainWindowViewModel.Execution.cs:171`; it must now show the same statuses
     the live events did, including Commit `done`).
5. **Consequences to verify.** A completed task shows all 11 stages "Complete"
   (`StageRowViewModel.StatusLabel` maps `"Done" → "Complete"`,
   `StageRowViewModel.cs:34`), Commit included. A flagged task shows the flagged
   stage "Flagged", earlier stages "Done", later stages "Waiting".

## Done when

- [ ] The driver writes a status entry for **every** stage it reaches, including
      the driver-only Commit stage (11): `running` at start, `done` with metrics +
      `check` at done, `flagged` with `error` on flag, `waiting` for not-yet-run
      stages. Covered by a driver test asserting the `status.json` contents.
- [ ] A **completed** run shows all 11 stages "Complete" in the archived view
      (including Commit) — covered by a test that runs/loads a committed task and
      asserts every `StageRowViewModel.StatusLabel == "Complete"`. This fails on
      current `main` (all stages read "Flagged", Commit reads "Waiting").
- [ ] A **flagged** run shows the flagged stage "Flagged", earlier stages "Done",
      and later stages "Waiting" — covered by a test asserting the per-stage
      statuses for a run that flags at a mid-pipeline stage, plus
      `SelectedTaskError` carrying that stage's error.
- [ ] Stage status is read **from the status record, not re-parsed from
      `result.outcome`** — the `outcome == "ok"` heuristic in
      `RelayRunHistory.ReadResult` is deleted or no longer used for status, so the
      `"success"` ≠ `"ok"` misclassification cannot recur. Covered by a test that
      a report with `result.outcome == "success"` / `exit_code 0` yields a "Done"
      stage.
- [ ] The status record is added to the force-committed proof set (or, if you
      deliberately keep it local, that decision is documented and the archived
      status still loads from it after a run); covered/verified consistently with
      how `proofFiles` is built in `RelayDriver.cs:177` and committed via
      `GitCommitter` (`GitCommitter.cs:60-62`).
- [ ] Metric display (time/cost/turns/model) is **unchanged** for every existing
      case — the numeric labels still come from the report-derived
      `StageRunMetric`; verify existing `RelayRunHistory`/`StageRowViewModel`
      metric assertions still hold.
- [ ] All new/updated tests were written to **fail first** against current `main`,
      then pass.
- [ ] `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
      Commit subjects.
