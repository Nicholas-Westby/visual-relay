## Stage 1 - Ideate

{
  "summary": "Clear `SelectedTaskError` at run start and suppress mid-run re-population so the stale 'LATEST RUN FAILED' banner does not persist while a new run is in progress. Three options differ in where the suppression rule is placed: (A) guard in LoadRunHistoryAsync, (B) guard in HasSelectedTaskError getter, (C) clear inside ResetStages() with the same LoadRunHistoryAsync guard.",
  "options": [
    "Option A — Clear SelectedTaskError=null at top of RunOneAsync; add `if (_runningTaskId == taskId) return` guard in LoadRunHistoryAsync to prevent disk-reload from restoring the stale error mid-run.",
    "Option B — Clear SelectedTaskError=null at top of RunOneAsync; modify HasSelectedTaskError getter to return false when the selected task is the currently running task, keeping LoadRunHistoryAsync untouched.",
    "Option C — Move clear into ResetStages() (called by RunOneAsync); add same LoadRunHistoryAsync guard as Option A. Requires verifying the task-switch path (ResetStages→LoadRunHistoryAsync) is not broken."
  ]
}

## Stage 2 - Research

{
  "findings": "The stale error banner is caused by two gaps: (1) `RunOneAsync` (Execution.cs:149-152) calls `ResetStages()`, `ClearLogState()`, `BeginRunningTask(task)` but never clears `SelectedTaskError`, so the prior failure's message persists throughout the new run. (2) If the user navigates away and back to the running task, `LoadSelectedTaskAsync` (Commands.cs:147) → `LoadRunHistoryAsync` (RunHistory.cs:7) re-reads the on-disk reports, which (until the in-progress stage writes its own attempt) is still the prior attempt's errored report — so the stale banner reappears mid-run. The fix requires: (a) `SelectedTaskError = null` at the top of `RunOneAsync` (alongside `ResetStages`/`ClearLogState`), and (b) a guard in `LoadRunHistoryAsync` that skips setting `SelectedTaskError` when `_runningTaskId == taskId` (i.e., the selected task is actively running). The existing test `SelectingTask_SurfacesErrorFromFailedLatestRunAndClearsOnCleanTask` (Tests.cs:140-162) validates settled-task behavior and must not regress. All source files are well under the 300-line limit (Execution.cs=178, RunHistory.cs=41, ViewModel.cs=229, Commands.cs=205, Helpers.cs=277, LiveState.cs=93, Tests.cs=242). The `_runningTaskId` field (ViewModel.cs:31) is already available for the mid-run guard. Verification uses `./visual-relay check` (format → build → tests → screenshots).",
  "constraints": [
    "All .cs and .axaml files must stay under 300 lines (enforced by tools/guards/check-file-size.sh, default limit 300, configurable via VISUAL_RELAY_FILE_LINE_LIMIT)",
    "`./visual-relay check` must pass: format verification (dotnet format --verify-no-changes), build, all tests, and headless screenshots",
    "`./visual-relay screenshot` must succeed at the end (generates docs/images/visual-relay-main.png and visual-relay-compact.png)",
    "Conventional Commit required for the final git commit message",
    "Write the failing unit test first (TDD: red-green). The test must: select a task with a failed latest run → simulate starting a run → assert SelectedTaskError is null / HasSelectedTaskError is false → simulate a failing run settling → assert error is set again",
    "No regression to existing test `SelectingTask_SurfacesErrorFromFailedLatestRunAndClearsOnCleanTask` — a failed-and-not-running task must still show its error",
    "No mocking library is available; tests rely on concrete test doubles (TestRepository, etc.) and filesystem fakes",
    "The `_runningTaskId` field is internal state set by BeginRunningTask and cleared by ClearRunningTask — the guard in LoadRunHistoryAsync must check `_runningTaskId == taskId`",
    "ResetStages() is called in both RunOneAsync and LoadSelectedTaskAsync — clearing SelectedTaskError inside ResetStages would be overwritten by LoadRunHistoryAsync in the task-switch path, so the clear must be explicit in RunOneAsync",
    "The guard in LoadRunHistoryAsync must leave the metric label and stage board data intact (only skip SelectedTaskError reassignment)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The stale-error-banner bug has two gaps, both confirmed in source. Gap 1: `RunOneAsync` (Execution.cs:149-152) calls `ResetStages()`, `ClearLogState()`, `BeginRunningTask(task)` but never clears `SelectedTaskError`, so the prior failure's text persists for the entire new run. The only location that clears `SelectedTaskError` is the no-task early-return in `LoadSelectedTaskAsync` (Commands.cs:154). Gap 2: mid-run navigation calls `LoadSelectedTaskAsync` → `LoadRunHistoryAsync` (RunHistory.cs:12-15), which reads on-disk reports; until the in-progress stage writes its own attempt, the disk still carries the previous attempt's errored report, so `SelectedTaskError` gets repopulated even though `_runningTaskId` (set via `BeginRunningTask` at LiveState.cs:15-22) already identifies this task as actively running. `LoadRunHistoryAsync` is only called after the run finishes (Execution.cs:164), so the stale error stays for the whole run. The banner is rendered by `TaskDetailPanel.axaml:74-88` bound to `HasSelectedTaskError`/`SelectedTaskError` (ViewModel.cs:130-133). The existing test at MainWindowViewModelTests.cs:140-162 validates settled-task error display and must not regress.",
  "excerpts": [
    "RunHistory.cs:12-15 — SelectedTaskError is set from the last errored stage's error_message, read from on-disk report files that may still be the previous attempt's during a new run.",
    "Commands.cs:151-157 — the no-task branch is the only place SelectedTaskError is cleared; the task-present branch (line 164) calls LoadRunHistoryAsync which re-derives the error from disk.",
    "Execution.cs:147-152 — RunOneAsync begins a run with ResetStages + ClearLogState + BeginRunningTask but leaves SelectedTaskError unchanged.",
    "Execution.cs:160-176 — LoadRunHistoryAsync is only called after driver.RunTaskAsync completes (line 164), not at run start.",
    "LiveState.cs:15-22 — BeginRunningTask sets _runningTaskId; LiveState.cs:38-59 — ClearRunningTask clears it in the finally block.",
    "TaskDetailPanel.axaml:74-88 — the red 'LATEST RUN FAILED' banner is bound to HasSelectedTaskError / SelectedTaskError.",
    "ViewModel.cs:130-133 — SelectedTaskError property and HasSelectedTaskError computed property.",
    "MainWindowViewModelTests.cs:140-162 — existing test SelectingTask_SurfacesErrorFromFailedLatestRunAndClearsOnCleanTask validates settled-task behavior."
  ],
  "repro": "1. Create a task with a failed stage report on disk (error_message set). 2. Select that task — the red 'LATEST RUN FAILED' banner appears with the stale error. 3. Start a new run on that same task. 4. Observe: the banner remains visible showing the old error, even though the queue card now says 'Running'. 5. Navigate to another task and back — the banner reappears because LoadRunHistoryAsync re-reads the old report from disk."
}

## Stage 4 - Plan

{
  "plan": "## Fix: stale-error-banner-persists-on-new-run\n\n### Change 1 — Clear `SelectedTaskError` when a run starts\n**File:** `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` line 150\n**Old:**\n```csharp\n        ResetStages();\n        ClearLogState();\n        StatusText = $\"Running {task.Id}\";\n```\n**New:**\n```csharp\n        ResetStages();\n        ClearLogState();\n        SelectedTaskError = null;\n        StatusText = $\"Running {task.Id}\";\n```\n\n### Change 2 — Suppress stale error re-population mid-run\n**File:** `src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs` lines 12-16\n**Old:**\n```csharp\n        SelectedTaskError = metric.Stages\n            .Where(stage => !stage.Succeeded)\n            .OrderByDescending(stage => stage.StageNumber)\n            .Select(stage => stage.ErrorMessage)\n            .FirstOrDefault();\n```\n**New:**\n```csharp\n        if (_runningTaskId != taskId)\n        {\n            SelectedTaskError = metric.Stages\n                .Where(stage => !stage.Succeeded)\n                .OrderByDescending(stage => stage.StageNumber)\n                .Select(stage => stage.ErrorMessage)\n                .FirstOrDefault();\n        }\n```\n\n### Change 3 — New unit test (TDD: red first, then green)\n**File:** `tests/VisualRelay.Tests/MainWindowViewModelTests.cs` after line 162\nInsert new test `StartingRunOnPreviouslyFailedTask_ClearsStaleErrorAndRestoresAfterRunSettles` that:\n1. Creates a repo with a failed task (errored report on disk)\n2. Selects the task → asserts `HasSelectedTaskError` is true\n3. Calls `RestoreRunningTaskState(\"broken\", 1, \"Research\")` to simulate a run starting\n4. Navigates away (null) and back to trigger `LoadRunHistoryAsync` → asserts `HasSelectedTaskError` is false (mid-run guard suppresses stale error)\n5. Switches running state to `\"_cleared_\"` to simulate run completion\n6. Navigates away and back again → asserts `HasSelectedTaskError` is true (error returns after run settles)\n\n### Verification\n- `./visual-relay check` green (format → build → tests → screenshots)\n- `./visual-relay screenshot` succeeds\n- All files under 300 lines: Execution.cs 179, RunHistory.cs 44, Tests.cs ~287\n- No regression to `SelectingTask_SurfacesErrorFromFailedLatestRunAndClearsOnCleanTask` (failed-and-not-running task still shows error because `_runningTaskId` is null)",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs",
    "tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ],
  "rationale": "TDD red-green cycle completed. Added test `StartingRunOnPreviouslyFailedTask_ClearsStaleErrorAndRestoresAfterRunSettles` (line 164) that: (1) selects a failed task → asserts HasSelectedTaskError=true, (2) simulates run start via RestoreRunningTaskState → navigates away/back → asserts HasSelectedTaskError=false (mid-run guard), (3) simulates run completion → navigates away/back → asserts HasSelectedTaskError=true (settled error returns). Two production fixes: `SelectedTaskError=null` added at RunOneAsync:151 to clear stale error at run start; guard `if (_runningTaskId != taskId)` at RunHistory.cs:12-19 suppresses stale-error re-population when LoadRunHistoryAsync re-reads the old on-disk report mid-run. All 125 tests pass, `./visual-relay check` green, screenshots render correctly, all files under 300 lines."
}

## Stage 6 - Implement

{ "summary": "All Stage 6 changes were already implemented in prior stages. Verified: (1) Execution.cs:151 clears SelectedTaskError at run start; (2) RunHistory.cs:12-19 guards against mid-run stale-error repopulation via _runningTaskId check; (3) Tests.cs:164-203 adds StartingRunOnPreviouslyFailedTask_ClearsStaleErrorAndRestoresAfterRunSettles test. All 125 tests pass, ./visual-relay check green, screenshots render, all files under 300 lines." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All three fixes from the plan are implemented and verified. Change 1 (Execution.cs:151): SelectedTaskError=null clears stale error at run start. Change 2 (RunHistory.cs:12-19): _runningTaskId guard suppresses stale-error re-population mid-run. Change 3 (Tests.cs:164-203): TDD test validates clear-on-start and restore-after-settle. 125/125 tests pass, build clean, screenshots render, all files under 300 lines." }

## Stage 9 - Verify

{
  "summary": "Fix stale-error-banner-persists-on-new-run: cleared SelectedTaskError at RunOneAsync start (Execution.cs:151) and added _runningTaskId guard in LoadRunHistoryAsync (RunHistory.cs:12-19) to prevent mid-run re-population from old on-disk reports. TDD test StartingRunOnPreviouslyFailedTask_ClearsStaleErrorAndRestoresAfterRunSettles validates all three states (settled-failed→error visible, running→error hidden, settled-failed again→error visible). 125/125 tests pass, ./visual-relay check green, screenshots render, all files under 300 lines.",
  "commitMessage": "fix: clear stale error banner when a new run starts, suppress mid-run re-population\n\nSelectedTaskError is now nulled at the top of RunOneAsync alongside\nResetStages/ClearLogState, and LoadRunHistoryAsync skips reassignment\nwhen the selected task is the actively running one (_runningTaskId == taskId).\nThe red \"LATEST RUN FAILED\" banner no longer persists from a prior\nfailed run while a new run is in progress.\n\nCloses #stale-error-banner-persists-on-new-run"
}

## Stage 10 - Fix-verify

{ "summary": "All three changes verified in place: (1) Execution.cs:151 clears SelectedTaskError=null at run start; (2) RunHistory.cs:12-19 guards against mid-run stale-error re-population via _runningTaskId check; (3) Tests.cs:164-203 validates clear-on-start, suppress-mid-run, restore-after-settle. Build clean (0 warnings/errors), 125/125 tests pass, screenshots render, all files under 300 lines. Fix confirmed." }

## Stage 11 - Commit

Committed by Visual Relay.

