# Resume an incomplete relay run from the last completed stage

Today there is **no resume**. The driver always restarts from stage 1: the stage loop is
`foreach (var stage in RelayStages.All)` (`src/VisualRelay.Core/Execution/RelayDriver.cs:55`)
with no check for stages already completed in a prior run. Re-running a task allocates fresh
attempt indices via `RelayAttempt.Next()`
(`src/VisualRelay.Core/Traces/RelayAttempt.cs:42-60`, called at `RelayDriver.cs:208`) and
redoes **every** stage (see `RunTaskAsync_AllocatesNextAttemptIndexOnEachReRun`). Per-stage
outputs persist (`stageN-attemptM.report.json`, `<task>.seals`, `manifest.txt`) but are never
read back to skip work.

So a run that times out at stage 10 of 11 re-does stages 1–10 on the next run — wasteful, and
with long stages it tends to re-hit the same timeouts before reaching the unfinished work. (The
`new-task-editor-in-detail-pane` run reached stage 9 green and then died at stage 10; resuming
it would have been a single stage, not a full re-run.)

## What to build

- **Resume a task's most recent incomplete run from the last successfully-completed stage**,
  reusing prior stages' outputs instead of redoing them. Re-run the failed/incomplete stage
  (fresh attempt) and continue forward; earlier `done` stages are skipped and their artifacts
  reused.
- **Source of truth for "last completed stage":** build on the authoritative per-stage status
  record from the companion task `persist-stage-status-record.md`
  (`.relay/<task>/status.json`, `done`/`flagged`/`waiting` per stage) rather than re-deriving
  completion from report outcomes. Sequence this task after, or coordinate with, that one.
- **Preserve downstream context.** Later stages' prompts are built from prior stages' reports
  (`RelayDriver` assembles each stage invocation from earlier outputs) — a resumed run must
  feed the skipped stages' existing reports into the stages that follow, exactly as a fresh run
  would.
- **Keep artifacts continuous.** The seal hash-chain appends one entry per stage
  (`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs`); resuming must extend the chain,
  not corrupt or restart it. Manifest/ledger continuity and attempt-index allocation must not
  clobber prior attempts.
- **Trigger explicitly.** A flag/command (e.g. `run-task --resume`) and a UI "Resume"
  affordance, distinct from a clean re-run. Default behavior is unchanged: a normal re-run
  still starts from stage 1.
- **Edge cases:** a stale `ACTIVE/info.json` from a crashed run; a task with no prior run
  (resume == fresh run); a prior run that flagged at stage 1 (nothing to skip).

## Done when

- A task whose prior run flagged/timed out at stage N can be resumed: stages 1…N-1 are skipped
  (their prior outputs reused), stage N onward re-runs. Covered by a test.
- A normal re-run (no resume flag) still starts from stage 1 — unchanged. Covered by a test.
- Resume reads completion from the authoritative status record
  (`persist-stage-status-record.md`), not re-parsed report outcomes.
- The seal chain, manifest, and ledger remain valid across a resumed run.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
