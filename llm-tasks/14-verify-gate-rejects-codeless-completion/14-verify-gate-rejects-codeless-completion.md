# The Completion Gate Must Reject a Code-Requiring Task That Produced No Code

A task can currently be marked **DONE** — spec renamed to `DONE-<id>.md`, moved into `completed/`, and
committed — with **zero source or test changes**. This actually happened: the "centralize colors and font
sizes into design tokens" task was auto-retired to DONE by commit `0e12f23`, which staged **only**
`.relay/**` proof artifacts plus the spec rename. None of the files that task's spec required (a new
`src/VisualRelay.App/Theme/Colors.axaml`, `DesignTokenAccessibilityTests.cs`, etc.) were created — and none
exist in the tree today (`App.axaml` still has only `<Application.Styles>`, there is no `Theme/` directory,
and there are zero `DynamicResource` usages). The pipeline reported success for a task that changed no code.
Close this hole: a task expected to produce code must not complete unless the working tree actually changed.

## Current state — how a task completes, and where the check is missing

**Stage pipeline.** `src/VisualRelay.Core/Execution/RelayDriver.cs`, `RunTaskAsync(...)` loops
`foreach (var stage in RelayStages.All)` (the stage table is `RelayStages.All` in
`src/VisualRelay.Core/Execution/RelayStages.cs`; stage 9 = "Verify", 10 = "Fix-verify", 11 = "Commit"),
then runs the commit stage and returns `RelayTaskOutcome(..., RelayTaskOutcomeStatus.Committed, ...)`.
"Succeeded" = stages 1–10 green + stage 11 commit success. **None of the success conditions reference a
diff, a changed-file count, or a non-empty manifest.**

**Verify gate (stage 9).** `src/VisualRelay.Core/Execution/RelayDriver.Stage9.cs`,
`RunStage9PreAgentAsync` runs the isolated test suite. Pass/fail is computed in the `stage.Number == 9`
block of `RelayDriver.cs`:
```csharp
var stage9Red = stage9TestResult!.ExitCode != 0 || stage9BootstrapFailed
    || stage9GuardFailed || stage9NewGuardOutput is not null;
```
Under `config.BaselineVerify` this is further filtered so only failures **new** vs the HEAD baseline count
(`RelayDriver.BaselineVerify.cs`). Crucially it asserts only that the **test suite exits 0** — so a
**clean, unchanged working tree passes trivially**, stage 9 goes green for a no-op, and green auto-skips
stage 10 ("Verify passed; nothing to fix").

**Completion / DONE.** `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`, `RetireAsync(...)` renames
`<id>.md` → `DONE-<id>.md` and moves it into `completed/batch-N/`. In the pipeline this is called from
`src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`, `ExecuteCommitStageAsync(...)` (stage 11),
which then commits via `src/VisualRelay.Core/Execution/GitCommitter.cs`, `CommitAsync(...)`.

**Why the commit can't catch it.** `GitCommitter.CommitAsync` stages the stage-4 manifest, runs
`git add -u`, then **force-adds proof files** (`.relay/<id>/ledger.md`, `.seals`, `manifest.txt`,
`status.json` — always present and new every run), then any new authored files. Because the proof
artifacts + the spec rename are **always** staged, `git commit` is never "nothing to commit" and
**succeeds even when zero source/test files changed**. The one post-commit invariant,
`FindUncommittedAuthoredFilesAsync`, checks the *opposite* direction (did we forget to commit a file the
run authored?) — never "did the run author anything?".

**The guard does not exist.** Nothing in the pipeline requires the tracked tree/source to have changed
(confirmed by grepping `Execution/` for `diff`, `HasChanges`, `manifest.Count`, "nothing to commit"). The
closest-related code serves other purposes and does not gate completion:
- `src/VisualRelay.Core/Execution/RelayDriver.Stage5.cs`, `HandleStage5Async` computes
  `hasImpl = manifest.Any(f => !testFiles.Contains(f) && IsImpl(f))` — but only to decide whether to run
  the author-tests red-gate; an empty or test-only manifest short-circuits to green with no requirement
  that anything change.
- `src/VisualRelay.Core/Execution/EarlyImplementationDetector.cs`, `ImplementationAlreadyUnderwayAsync`
  uses `git diff --quiet HEAD -- <impl files>` — but to *downshift* stage 6 when code was written too
  early (the inverse concern).
- `src/VisualRelay.Core/Execution/PlanCompletenessGate.cs`, `CheckCoverage` (invoked via
  `TryPlanCompletenessRetryAsync` in `RelayDriver.Snapshot.cs`) — extracts the spec's `## Done when` /
  `## Deliverables` bullets and checks their *tokens* appear in the plan narrative or manifest **text**.
  This is the nearest thing to a "spec requires X" check, but it verifies only textual coverage, issues a
  single soft retry, returns `null` when no checklist is present, and **never verifies a file was created
  or the tree changed**.

**No per-task "expects code" flag exists.** `src/VisualRelay.Domain/RelayTaskItem.cs` and
`src/VisualRelay.Domain/RelayConfig.cs` have no task-type / read-only / expects-code field; "read-only"
exists only at the **stage** level (`RelayStageDefinition.Files` = `none|some|all`). So the guard must
*derive* "this task was expected to produce code" itself. The building blocks already exist: `IsImpl` /
`IsTestFile` (`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs`), the stage-4 `manifest`, and
`runBaseSha` (captured via `CaptureRunBaseShaAsync` in `RunTaskAsync`).

## What to build

Add a completion gate that **fails a run which was expected to produce code but left the tracked source/
test tree unchanged.** Suggested shape (implementer to refine):

1. **Decide "expected to produce code."** Treat a task as code-expecting if **either** its stage-4
   `manifest` contains an implementation file (reuse `IsImpl`, excluding test-only) **or** the spec has a
   `## Deliverables` / `## Done when` checklist (reuse `PlanCompletenessGate`'s checklist extraction). If
   neither holds, treat it as a legitimately non-code task and skip the gate.
2. **Require a real change.** Diff the working tree (or HEAD) against `runBaseSha`, **restricted to tracked
   source/test paths — excluding `.relay/**` (proof artifacts) and `llm-tasks/**` (the spec rename)** (tune
   the exclusion set as needed, e.g. `VERSION`). If a code-expecting task has an **empty** such diff, fail
   it via the existing `FlagAsync` path with a clear reason ("task expected to modify source/tests but
   produced no changes") instead of committing and retiring to `DONE-`.
3. **Placement.** Natural homes: fold it into the stage-9 `stage9Red` computation in `RelayDriver.cs` (make
   "green" also require a non-empty code diff for code-expecting tasks), **or** add it in
   `ExecuteCommitStageAsync` (`RelayDriver.CommitGate.cs`) immediately **before**
   `TaskCompletionArchive.RetireAsync` (refuse to retire/commit-as-done a no-op). Prefer whichever keeps the
   flag path consistent with existing failures.
4. **Avoid false positives.** Legitimately read-only / observational tasks exist here (e.g. an
   "observational verify" task). With no task-type flag, the manifest/checklist heuristic in (1) is what
   distinguishes them — a purely observational task has no impl files in its manifest and no
   code-implying checklist. If that heuristic proves too blunt, the cleaner long-term fix is an explicit
   per-task marker (spec front-matter field, or a `RelayTaskItem` property) that the gate reads; note this
   as the better long-term option, but the heuristic is acceptable for this task.

## Constraints & done criteria

- A **code-expecting task that produces no source/test change is flagged** (not committed, not retired to
  `DONE-`), with a clear reason.
- The "did code change?" diff **excludes `.relay/**` and `llm-tasks/**`** (and any other pure-bookkeeping
  paths), so proof artifacts and the spec rename alone do not satisfy it.
- **Reproduce-the-original-bug test:** simulate a code-expecting run whose only changes are `.relay/**`
  proof + the spec rename (empty src/test diff) and assert it is **flagged, not marked done**. Add a
  positive test (a real source change passes) and a **read-only-task test** (an observational task with no
  impl manifest still completes — no false positive).
- Reuse existing building blocks (`IsImpl`, the stage-4 `manifest`, `runBaseSha`, `PlanCompletenessGate`
  checklist extraction) rather than new git plumbing where possible.
- Keep every edited file within the **≤300-line** gate. Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.Core/Execution/RelayDriver.cs` and/or `src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`
  — add the "code was actually produced" gate (stage-9 green criterion, or a pre-retire check).
- `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs` — reuse `IsImpl`; add a helper to diff the
  manifest's impl files against `runBaseSha`, excluding `.relay/**` and `llm-tasks/**`.
- `tests/VisualRelay.Tests/` — phantom-completion (flagged), real-change (passes), and read-only
  (no false positive) tests.
- (reference, no change) `src/VisualRelay.Core/Execution/RelayDriver.Stage5.cs` (`hasImpl`),
  `src/VisualRelay.Core/Execution/PlanCompletenessGate.cs`, `src/VisualRelay.Core/Execution/RelayDriver.Snapshot.cs`,
  `src/VisualRelay.Core/Execution/GitCommitter.cs`, `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`,
  `src/VisualRelay.Core/Execution/RelayStages.cs`.
- (possible, cleaner long-term) `src/VisualRelay.Domain/RelayTaskItem.cs` — an explicit "expects code /
  read-only" marker, if the heuristic proves insufficient.
