# Parallelize the planning stages (Ideate/Research/Diagnose/Plan) across tasks

Visual Relay runs the whole queue strictly one task at a time: `RelayQueueController.DrainAsync`
(`src/VisualRelay.Core/Queue/RelayQueueController.cs`) and the app's mirror
`MainWindowViewModel.DrainQueueAsync` (`MainWindowViewModel.Execution.cs`) both `await`
one `RunTaskAsync` to completion before starting the next, and `RelayDriver.RunTaskAsync`
acquires a repo-global `ActiveTaskLock` (`.relay/ACTIVE`) that *throws* `"relay: another task
is already active"` if a second run starts (`ActiveTaskLock.cs`). So when ten tasks are queued,
nine sit idle while one runs — including the early **read-only planning stages** that don't
touch the working tree at all.

The first four stages are read-only by design (`RelayStages.cs`):

| # | Stage | tier | files | commands | writes working tree? |
|---|-------|------|-------|----------|----------------------|
| 1 | Ideate   | cheap    | none | git,ls,cat | no (hard-restricted) |
| 2 | Research | cheap    | some | all        | no (instructed only) |
| 3 | Diagnose | balanced | some | all        | no (instructed only) |
| 4 | Plan     | balanced | some | all        | no (instructed only) |

They only **read** the repo and write their own per-task artifacts under `.relay/<taskId>/`
(ledger, `manifest.txt`, `status.json`, `<taskId>.seals`, `stageN-attemptM` reports/traces).
The git/working-tree mutation all lives in stages 5–11 (Author-tests, Implement, Review, Fix,
Verify, Fix-verify, Commit). That makes stages 1–4 safe to run **in parallel across many tasks**,
while stages 5–11 stay **serial** (one task at a time), exactly as today.

## Goal

Run the planning stages (1–4) for all pending tasks **concurrently**, bounded by a configurable
batch limit (default **10**), then run the mutating stages (5–11) **serially**, one task at a
time, each resuming from stage 5 using the planning artifacts it already produced. Within any one
task the stages still run in order; only *different* tasks overlap, and only during planning.

Net effect: a drain of N tasks does its N planning passes in parallel (wall-clock ≈ the slowest
single plan, not the sum), then commits them one-by-one with today's exact safety guarantees.

## Why this is safe — and the hard constraints

Research already nailed down the failure modes; the design must honor these:

1. **Commits must never be contaminated across tasks.** `GitCommitter.CommitAsync`
   (`GitCommitter.cs`) does `git reset -q` then `git add -u` — which stages **every** dirty
   tracked file in the repo, not just the task's manifest — plus an "untracked-since-run-start"
   delta. If two tasks ever had uncommitted edits in the same tree, one task's commit would
   **silently swallow the other's work**. The red gate (`RedGate.cs`, stage 5) and baseline
   verify (`GetNewFailuresAsync` in `RelayDriver.Artifacts.cs`, stage 9) `git stash` /
   `git checkout -- .` the **whole** tree. **Therefore stages 5–11 require exclusive ownership
   of the working tree and must stay serial.** This task must not weaken the
   `enforce-commit-authority` / `commit-authored-files-outside-manifest` behavior or the
   `.githooks/pre-commit` nonce gate; it must add a regression test proving two
   planned-then-executed tasks each commit **only their own** files.

2. **Planning must not write the shared tree.** Stages 2–4 carry `commands="all"` — the
   file-edit tools are sandboxed by `files=none/some`, but shell is an unguarded write escape, so
   today they are only *instructed* "Do not edit files," not *prevented*. The isolation approach
   below makes any stray planning write harmless.

3. **Resume tolerates time passing.** The seal / working-tree-hash chain is **append-only and
   never re-verified at runtime** (`WorkingTreeHash` in `RelayDriver.Artifacts.cs` is only ever
   *written*, never compared; the only `.seals` reader is `LoadResumeState`, which just continues
   the chain). So a task can plan against `HEAD_A`, and later — after earlier tasks have committed
   and advanced `HEAD` — resume at stage 5 with **no hash mismatch or rejection**. `firstStageToRun`
   is already derived from `status.json` (first non-`Done` stage), so "resume at stage 5 because
   1–4 are recorded Done" is exactly what the existing resume path does.

## Approach

### A. Isolation: a git worktree per planning task

Each parallel planning task runs in its **own ephemeral git worktree** checked out at the current
`HEAD`. This is the chosen isolation strategy because it makes *every* shared-resource hazard
disappear at once, with no surgery on the lock or the Swival invocation:

- The worktree is an independent working directory, so a stray shell write from a planning agent
  lands in a throwaway checkout, never the real tree.
- Swival anchors `swival.toml` and its `.swival/` scratch (incl. `trash/.lock` and a SQLite
  `cache.db`) to `--base-dir`. With `--base-dir` = the worktree path, each run gets its **own**
  `swival.toml` and `.swival/` — the create/delete race and `cache.db` contention vanish.
- The driver computes `taskDirectory = <rootPath>/.relay/<taskId>` and `ActiveTaskLock` lives at
  `<rootPath>/.relay/ACTIVE`. With `rootPath` = the worktree, **each planning run gets its own
  per-worktree `.relay/` and its own ACTIVE lock**, so the repo-global lock never collides and
  needs **no change**. Planning does no commits, so the pre-commit nonce gate is never exercised
  there.
- `.gitignore` ignores `.relay/*` (except the tracked `.relay/config.json`), `.swival/`, and
  `swival.toml`. So a worktree checked out at HEAD starts **clean** — it carries the tracked
  `config.json` (the planning driver loads config from the worktree with no extra wiring) and
  generates its own fresh `.relay/<taskId>/`, `.swival/`, and `swival.toml`; no other task's
  artifacts are present to read or clobber.

Worktree lifecycle (new, self-contained machinery — there is no worktree usage in the repo today):

- **Location:** create worktrees **outside** the main working tree (e.g. under
  `Path.GetTempPath()` namespaced by a hash of the repo root and the runId, like
  `{TMP}/visual-relay/wt/{repoHash}/{runId}`) so they never nest inside the repo or trip the
  file-size / source-enumeration guards.
- **Create:** `git worktree add --detach <wtPath> HEAD` (detached at HEAD → clean, no branch
  contention; multiple worktrees at the same commit are fine).
- **Run:** invoke the driver with `rootPath = <wtPath>`, `LastStageToRun = 4`,
  `CreateGitCommit = false`, and its **own** dependencies (own `SwivalSubagentRunner` + own
  per-task `EventSink`). Live progress reaches the GUI through the in-memory observable sink
  (which is path-independent), so the UI updates even though artifacts are on disk in the worktree.
- **Copy back:** on completion (Planned **or** Flagged), copy the worktree's
  `.relay/<taskId>/` subtree into the **main** repo's `.relay/<taskId>/` (ledger, manifest,
  status.json, seals, run.log, all `stageN-attemptM` report/trace dirs). `RelayTraceLocator`
  globs `.relay/<taskId>/stage*-attempt*` relative to the repo root, so trace history works after
  copy-back. Manifest entries are repo-relative and valid in the main tree unchanged.
- **Remove:** `git worktree remove --force <wtPath>`.
- **Crash recovery:** at drain start, `git worktree prune` and delete any leftover worktree
  directories from a previous crashed drain before starting a new one.

Complementary hardening (recommended, low-risk, stands on its own): tighten stages 2–4 in
`RelayStages.cs` from `commands="all"` to a read-only command whitelist (Ideate already uses
`git,ls,cat`; Research/Diagnose/Plan need only read/search/inspect commands). This belt-and-
suspenders prevents tree writes even inside the worktree and matches the documented intent of
these stages. If any planning capability genuinely needs a broader command, keep that command but
do not grant write-y ones.

### B. Driver: a plan-only bound + the existing resume

- Add `int? LastStageToRun = null` to `RelayDriverOptions` (`RelayDriverOptions.cs`, today just
  `record RelayDriverOptions(bool CreateGitCommit, bool Resume = false)`).
- In the stage loop in `RelayDriver.RunTaskAsync` (next to the existing
  `if (stage.Number < firstStageToRun) continue;`), add
  `if (_options.LastStageToRun is { } last && stage.Number > last) break;` — a `break`, so it exits
  before the post-loop commit block.
- Return a distinct outcome for a plan-only run (add `RelayTaskOutcomeStatus.Planned`) instead of
  reusing `Committed`/`simulated`. Stage 4 already persists `manifest.txt` during its own
  execution, and the loop records status (1–4 `Done`, 5–11 `Waiting`), so the artifacts needed for
  resume exist on disk when the plan-only run returns.
- The serial execute phase runs the **same** driver with `Resume = true, CreateGitCommit = true`,
  `rootPath` = the main repo. `LoadResumeState` reads the copied-back `status.json` and lands
  `firstStageToRun` on 5. (`commitMessages` is produced at stage 9, inside the execute session, so
  it is **not** affected by the plan/execute split.)

### C. Orchestration: two-phase drain with a batch limit

Make `RelayQueueController.DrainAsync` the single source of truth for the two-phase flow and have
the GUI call it (rather than maintaining the duplicate loop in `MainWindowViewModel.Execution.cs`
— consolidate to avoid two divergent drain loops):

- **Phase 1 — parallel plan.** For every pending task whose `status.json` does **not** already show
  stages 1–4 `Done` (so re-drains and crash-resumes don't re-plan), run plan-only in a worktree.
  Bound concurrency with a `SemaphoreSlim(maxPlanConcurrency)` and await the batch. A task that
  **flags** during planning (e.g. stage-4 rejects a manifest that points under `llm-tasks/`) gets
  its `NEEDS-REVIEW` copied back and is **excluded** from Phase 2 without blocking the rest of the
  batch.
- **Phase 2 — serial execute.** The existing serial `while` loop over the successfully-planned
  tasks, each with `Resume = true`, committing one at a time under the unchanged main `ActiveTaskLock`
  + pre-commit hook. The existing `DrainCircuitBreaker`, pause-at-boundary, and outcome handling
  stay as they are.
- **Config:** add `maxPlanConcurrency` (default 10) to `RelayConfig` / `RelayConfigLoader`
  (`.relay/config.json`), mirroring how `MaxVerifyLoops` / `BaselineVerify` are wired.
- **Cancellation / pause:** there is no `CancellationTokenSource` today (runs use `default`). Add a
  per-drain `CancellationTokenSource` so a pause/stop request cancels in-flight planning tasks at
  the batch level; honor the existing `RequestPause` semantics (finish/seal cleanly, stop before
  Phase 2 or at the next task boundary).

### D. Logging stays per-task (no confusing interleave)

Logging is already mostly per-task: each run writes `.relay/<taskId>/run.log`, and every
`RelayEvent` carries `RunId` + `TaskId`. Hardening:

- Give **each** parallel planning run its **own** `CompositeRelayEventSink` (its own
  `FileRelayEventSink` at the task's `run.log` + its own observable sink) — never share one sink
  instance across tasks. The `FileRelayEventSink` lock is per-instance, so per-task instances never
  interleave.
- Write the task id into the `FileRelayEventSink` line (it currently emits `run=<RunId>` but no
  standalone `task=` field) so a merged tail across tasks is greppable.
- Add a small **drain-level summary log** (e.g. `.relay/drain-<timestamp>.log`) that records only
  high-level per-task milestones (`plan_start` / `plan_done(stageN)` / `plan_flagged` / `execute_start`
  / `committed` / `flagged`) so there is one place to watch overall drain progress. Full per-stage
  traces stay in each task's own `run.log`. Mark the plan vs execute phase on these milestones.

### E. GUI: keep the current UI, just show many tasks planning

Today the GUI assumes a single active run: one shared `Stages` board, one `_runningTask` pointer,
one elapsed timer, and `ApplyRunningTaskToRows` actively marks every *other* task idle when one
starts (`MainWindowViewModel.LiveState.cs`); `IsBusy`/`RunBusyAsync` structurally serialize runs.
The desired behavior (unchanged look, just multi-task aware):

- **Left/queue panel:** each task row shows the phase/stage it is currently in (e.g. its stage
  name/number, "Planning…", then "Planned — queued") and updates **live**, with **several rows
  in progress at once** during Phase 1. Replace the single running-task pointer with a *set* of
  running tasks so starting task B no longer marks task A idle; drive each row from that task's own
  events/`status.json`.
- **Detail pane:** clicking any task row shows that task's full stage board, logs, traces, and
  per-stage progress/state **exactly as it does today** — including a task that is planning in the
  background. The per-task buffers `_liveEventsByTask` / `_liveTraceEntriesByTask` and
  `EventsFor(taskId)` / `TraceEntriesFor(taskId)` already capture every task's events regardless of
  selection; make the detail board/log rebuild from the **selected** task's buffer + `status.json`
  when selection changes, instead of from a single global board that the running task overwrites.
- Fix the blockers so nothing regresses: don't mark other rows idle; let the drain (not `IsBusy`)
  own concurrency; make elapsed time per-task (or at least don't break with multiple runs); scope
  `ResetStages()` / `ClearLogState()` to the selected task so background tasks' buffers aren't wiped.
- Single-task runs (Run / Resume on one task) must look and behave exactly as today.

## Files

- `src/VisualRelay.Core/Execution/RelayDriverOptions.cs` — add `LastStageToRun`.
- `src/VisualRelay.Core/Execution/RelayDriver.cs` — `break` past `LastStageToRun`; return `Planned`.
- `src/VisualRelay.Domain/RelayTaskOutcome.cs` — add `Planned` outcome status.
- `src/VisualRelay.Core/Execution/RelayStages.cs` — (recommended) read-only command whitelist for
  stages 2–4.
- New: a worktree-pool / planning-isolation helper in `src/VisualRelay.Core/Execution/`
  (create/prune/remove worktrees, copy `.relay/<taskId>/` back). Keep `RelayDriver.cs` and any
  new file under the 300-line guard — extract helpers as needed.
- `src/VisualRelay.Core/Queue/RelayQueueController.cs` — two-phase `DrainAsync` (parallel plan with
  `SemaphoreSlim`, then serial resume-from-5); per-drain `CancellationTokenSource`.
- `src/VisualRelay.Domain/RelayConfig.cs` + `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`
  — `maxPlanConcurrency` (default 10).
- `src/VisualRelay.Core/Logging/FileRelayEventSink.cs` — add `task=` to the line; drain summary log.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel*.cs` (`.Execution`, `.LiveState`, `.Helpers`,
  `StageRowViewModel`, `TaskRowViewModel`) — multi-task running state; consolidate the GUI drain
  onto `RelayQueueController.DrainAsync`.
- `tools/VisualRelay.RunTask/Program.cs` — (optional) expose `--plan-only` / `--resume` for smoke
  testing the two phases from the CLI.

## Tests (write the failing tests first)

Use the existing mocked doubles (`ScriptedSubagentRunner`, the injected test runner, the
Avalonia.Headless harness in `tests/VisualRelay.Tests/`):

- **Plan-only bound:** a driver with `LastStageToRun = 4, CreateGitCommit = false` runs stages 1–4,
  writes `manifest.txt` + `status.json` (1–4 `Done`, 5–11 `Waiting`), creates no git commit, and
  returns `Planned`.
- **Resume-from-5:** after a plan-only run, a `Resume = true` run starts at stage 5, completes
  through commit, and the seal chain continues unbroken.
- **Batch limit:** with a counting fake subagent runner, no more than `maxPlanConcurrency` planning
  runs are ever in flight at once.
- **Per-task artifact isolation:** N tasks planned concurrently each produce their own
  `.relay/<taskId>/` artifacts; none writes into another's directory; a planning agent that shells
  a write (simulated) does not modify the main working tree or another task's worktree.
- **No commit contamination (the key one):** two tasks are planned, then executed serially; each
  resulting commit contains **only its own** manifest/authored files — extend the existing
  commit-authority / authored-files-outside-manifest tests to the parallel-plan path.
- **Lock behavior:** parallel planning never throws `"another task is already active"`; the serial
  execute phase still serializes commits one at a time.
- **Idempotent re-plan:** re-draining a queue where some tasks already show stages 1–4 `Done` skips
  re-planning them and proceeds to execute.
- **Logging:** events from concurrently-planning tasks land in their own `run.log` with no
  interleaving; the drain summary log records per-task milestones with the phase marked.
- **GUI (headless):** starting a drain with ≥3 tasks shows multiple rows progressing through phases
  without any row marking the others idle; selecting a background (planning) task renders its stage
  board/log from its own buffer; a single-task run behaves exactly as before.

## Done when

- A queue drain plans all pending tasks in parallel (≤ `maxPlanConcurrency` at once), then executes
  and commits them serially, with each task's stages still ordered 1→11.
- Stages 5–11 retain exclusive working-tree ownership; commits are byte-for-byte as clean as today
  and never include another task's changes (proven by test).
- Each planning task is isolated in its own git worktree; worktrees are pruned/removed on
  completion and on the next drain after a crash; no worktree dirs leak into the repo.
- Resume-from-5 works after planning even though `HEAD` may have advanced (no seal/hash rejection).
- Logs remain per-task and readable; a single drain summary log shows overall progress.
- The GUI shows each task progressing through its current phase in the left panel with several in
  progress at once, and the detail pane shows the selected task's full stages/state as it does now;
  single-task runs are unchanged.
- `./visual-relay check` is green; C#/XAML files stay under the 300-line guard; Conventional Commit
  subjects.

## Out of scope / follow-ups (note, don't build)

- **Parallelizing stages 5–11.** They mutate the shared tree and stay serial. (A future option is
  to keep each task in its worktree through implementation and integrate via merge/rebase, but that
  is a separate, larger task with real conflict-resolution design.)
- **Plan staleness.** A task plans against `HEAD_A`; if an earlier-executed task changes the same
  files, the *plan prose* may be slightly stale by the time this task executes. The mutating stages
  (Author-tests/Implement) operate on live file content and Review/Verify run against the live tree,
  so this is acceptable. A future "re-plan if manifest files changed since planning" guard could
  tighten it — out of scope here.
- **A live multi-board dashboard** (every in-flight task's full stage board visible at once). The
  detail pane stays single-selected-task by design.

## Notes

- Keep `RelayDriver.cs` and any new orchestration/worktree file under the 300-line guard
  (`tools/guards/check-file-size.sh`); extract helpers (e.g. a `PlanningWorktree` /
  `PlanPhaseRunner` class) rather than growing existing files.
- Planning never invokes the target project's test/build command — `config.TestCommand` /
  `config.TestFileCommand`, whatever they are for the codebase Visual Relay is pointed at (the test
  runner is first called at stage 5's red gate). So Phase 1 spawns no concurrent heavyweight
  test/build processes regardless of language; that cost stays in the serial execute phase. Keep it
  that way.
- N concurrent agents increase load on the local LiteLLM proxy; it is a stateless HTTP server with
  provider fallbacks and Swival retries, so this is a throughput/rate-limit tuning concern, not a
  correctness one. The default `maxPlanConcurrency` of 10 is deliberately conservative; it is
  configurable.
- If a drain starts with uncommitted changes in the main tree, planning worktrees (checked out at
  `HEAD`) won't see them — planning is against the committed baseline, which is the intended
  behavior; surface this rather than silently merging.
