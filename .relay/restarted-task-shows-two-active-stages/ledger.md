## Stage 1 - Ideate

{
  "summary": "When restarting a task (resume=false), `ResetStages(task.Id)` reads the stale `status.json` from the previous run, resurrecting old \"Done\" stage statuses. These appear green alongside the new run's current \"Running\" stage, causing the visual bug of multiple active (green) stages. The fix: only read disk status when resuming; for a restart, just clear all stages to \"Waiting\".",
  "options": [
    "Option A — Conditional call in RunOneAsync: replace `ResetStages(task.Id)` with `if (resume) ResetStages(task.Id); else ResetStages();` (simplest, minimal change, keeps API stable)",
    "Option B — Add `clearOnly` parameter to ResetStages, then call `ResetStages(task.Id, clearOnly: !resume)` from RunOneAsync (more explicit but adds unused parameter for other call sites)",
    "Option C — Delete the old status.json on disk before calling ResetStages(task.Id) on restart (side-effect-y, writes to disk when not needed)"
  ]
}

## Stage 2 - Research

{
  "findings": "When a user restarts a task (resume=false), `RunOneAsync` calls `ResetStages(task.Id)` which reads the stale `status.json` from the previous run's on-disk record. The method first clears all stages to \"Waiting\" (correct), then re-populates statuses from the old file — resurrecting \"Done\" (green) stages from the prior run. Meanwhile, the new run fires `stage_start` events via `ApplyStageEventToBoard` (MainWindowViewModel.Helpers.cs:92-97) which sets the current stage to \"Running\" (also green). The result is multiple green stages simultaneously. On a resume this behavior is intentional (user sees where they left off). On a restart the old status is irrelevant. The fix (Option A) is simplest: change line 14 of `MainWindowViewModel.RunOne.cs` from `ResetStages(task.Id);` to `if (resume) ResetStages(task.Id); else ResetStages();`. The no-argument `ResetStages()` clears the board without reading disk. After the run completes, `LoadRunHistoryAsync` (RunHistory.cs:19-73) reads the freshly-written status.json and correctly populates the stage board, so final state is always accurate regardless.",
  "constraints": [
    "`ResetStages()` (no argument) is also called from `SelectTaskAsync` at lines 218 and 225 of `MainWindowViewModel.Commands.cs` — these are pure-clear paths that must not read disk; they remain unaffected by the change.",
    "`LoadRunHistoryAsync` (RunHistory.cs:43-58) re-reads the status record from disk after run completion and updates stage statuses/metrics. This ensures the board shows correct final state even after a restart clears everything to Waiting.",
    "`ApplyStageEventToBoard` (Helpers.cs:79-115) handles live `stage_start` events during a run, setting the active stage to \"Running\". With the fix, the board starts all-\"Waiting\" so this works correctly — the very first `stage_start` (stage 1) turns it green, and no stale \"Done\" stages compete.",
    "The `StageStatusRecord.Read` method (StageStatus.cs:53-70) returns an empty list when status.json is missing or unreadable, so the fix has no crash risk on tasks without prior runs.",
    "`RunSelectedAsync` (Execution.cs:30) calls `RunOneAsync(task)` without resume=true (defaults to false). `ResumeSelectedAsync` (Execution.cs:51) calls `RunOneAsync(task, resume: true)`. The two paths are distinct and the fix must preserve this distinction.",
    "The drain queue path (`DrainQueueAsync`, Execution.cs:57-146) does not call `ResetStages` directly — it uses `DrainLifecycleCallbacks` with `BeginRunningTask` — so it is unaffected.",
    "No existing tests cover the restart-vs-resume stage board behavior. Any fix must be validated manually or with new tests.",
    "The fix must not affect the `StageRowViewModel.MarkRunning` method (StageRowViewModel.cs:141-146) which sets Status to \"Running\" on `stage_start` — this is the correct live-update path that should continue working."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Root cause: `RunOneAsync` (MainWindowViewModel.RunOne.cs:14) calls `ResetStages(task.Id)` unconditionally — for both restart (`resume=false`) and resume (`resume=true`). `ResetStages` with a taskId argument (Stages.cs:20-39) reads the on-disk `status.json` file written by the previous run and repopulates stage statuses from it. On a restart, this resurrects prior-run \"Done\" entries (rendered green by `StageRowViewModel.AccentBrush` returning `SuccessBrush` for \"Done\" and `RunningBrush` for \"Running\" — both green `#5AD47D`). Meanwhile, the new run fires live `stage_start` events which `ApplyStageEventToBoard` (Helpers.cs:92-97) handles by calling `stage.MarkRunning()`, setting the current stage to \"Running\" (also green). The result: the UI shows the actively-running stage as green alongside the stale \"Done\" stages from the prior run's status.json — multiple active/green stages simultaneously. On resume this behavior is intentional (showing prior progress), but on restart the old status is irrelevant and should be discarded. After the run completes, `LoadRunHistoryAsync` (RunOne.cs:33) re-reads the freshly-written status.json and correctly populates all stages, so the final state is always accurate regardless. The fix: change line 14 of RunOne.cs from `ResetStages(task.Id);` to `if (resume) ResetStages(task.Id); else ResetStages();`. The no-argument `ResetStages()` clears all stages to \"Waiting\" without reading disk — exactly what a restart needs.",
  "excerpts": [
    "RunOne.cs:12-14: `private async Task RunOneAsync(TaskRowViewModel task, bool resume = false) { ResetStages(task.Id);` — called unconditionally for both restart and resume.",
    "Stages.cs:11-39: `ResetStages(string? taskId = null)` — lines 13-18 clear all to \"Waiting\", but lines 20-39 read `status.json` and overwrite with stale statuses when taskId is non-null.",
    "Execution.cs:30 vs 51: `RunSelectedAsync` calls `RunOneAsync(task)` (resume defaults to false), while `ResumeSelectedAsync` calls `RunOneAsync(task, resume: true)`. Both hit the same unconditional `ResetStages(task.Id)`.",
    "status.json (current run): stage 1 and 2 are `\"status\": \"Done\"`, stage 3 is `\"status\": \"Running\"`. On a restart this file is stale but `ResetStages(task.Id)` reads it anyway.",
    "StageRowViewModel.cs:69-75: `AccentBrush` returns `SuccessBrush` (#5AD47D) for \"Done\" and `RunningBrush` (#5AD47D) for \"Running\" — both visually green, indistinguishable.",
    "Helpers.cs:92-97: `ApplyStageEventToBoard` on `stage_start` calls `stage.MarkRunning(relayEvent.Timestamp)` which sets Status to \"Running\" (green) — the second source of green.",
    "RunOne.cs:33: `await LoadRunHistoryAsync(task.Id);` after driver completes — re-reads the fresh status.json and correctly repopulates all stages, confirming the final state will always be correct after the fix."
  ],
  "repro": "1. Run any task through at least one stage (so status.json has \"Done\" entries). 2. Click \"Run Selected\" (not \"Resume\") to restart the same task. 3. Observe the stage board: previously-completed stages show green (\"Done\") AND the current stage shows green (\"Running\") — multiple active/green stages visible simultaneously."
}

## Stage 4 - Plan

{
  "plan": "## Fix: Conditional ResetStages on restart vs resume\n\n**File: `src/VisualRelay.App/ViewModels/MainWindowViewModel.RunOne.cs`**\n- Line 14: Change `ResetStages(task.Id);` to `if (resume) { ResetStages(task.Id); } else { ResetStages(); }`\n\nOn restart (`resume=false`), `ResetStages()` clears all stages to \"Waiting\" without reading disk — no stale \"Done\" entries appear. On resume (`resume=true`), `ResetStages(task.Id)` reads `status.json` and rehydrates prior progress as intended. After the run completes (line 33), `LoadRunHistoryAsync` reads the freshly-written `status.json` and correctly populates all stages, so final state is always accurate regardless of the restart/resume path.\n\n**File: `tests/VisualRelay.Tests/MainWindowViewModelTests.cs`**\n- Add `RestartClearsStageBoard_DoesNotLoadStaleStatusFromDisk`: Writes a `status.json` with \"Done\" entries for a task, loads the VM, selects the task, asserts stages show \"Done\" (confirms disk→UI path works). Then sets `SelectedTask = null` (triggers `ResetStages()` in `SelectTaskAsync`) and asserts all stages are \"Waiting\". Then re-selects the task and asserts stages reload from disk as \"Done\". This validates the exact clear-then-rehydrate pattern the fix relies on.\n- Add `SelectTaskWithPriorRun_LoadsStageStatusesFromDisk`: Writes status.json with \"Done\", loads VM, selects task, asserts stages reflect Done — validates the resume/rehydrate path works correctly.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.RunOne.cs",
    "tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ]
}

> **Already-resolved**: no implementation delta to strip; accepted green regression coverage.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/MainWindowViewModelTests.cs"
  ],
  "rationale": "Added two tests to MainWindowViewModelTests.cs that validate the infrastructure the restart fix depends on:\n\n1. **SelectTaskWithPriorRun_LoadsStageStatusesFromDisk** — Writes a status.json with Done and Flagged entries, loads the VM, and asserts that selecting the task correctly rehydrates stage statuses from disk. Validates the LoadRunHistoryAsync rehydration path.\n\n2. **RestartClearsStageBoard_DoesNotLoadStaleStatusFromDisk** — Writes a status.json with Done entries, selects the task (asserts Done), deselects (asserts all Waiting via ResetStages() no-args clear), then re-selects (asserts Done reloaded from disk). Validates the exact clear-then-rehydrate pattern the fix will use: on restart (resume=false), ResetStages() without args clears the board to Waiting instead of ResetStages(taskId) reading stale status.json.\n\nBoth tests pass now because the SelectTaskAsync path already uses the correct no-args ResetStages() pattern. The fix in RunOneAsync line 14 will extend this same pattern to the restart path."
}

## Stage 6 - Implement

{
  "summary": "Fixed the bug where restarting a task showed multiple active (green) stages. Root cause: `RunOneAsync` unconditionally called `ResetStages(task.Id)`, which reads the stale `status.json` from the prior run and resurrects old \"Done\" (green) stage statuses. The fix in `MainWindowViewModel.RunOne.cs` line 14 changes the call to conditional: `if (resume) ResetStages(task.Id); else ResetStages();` — on restart, `ResetStages()` clears all stages to \"Waiting\" without reading disk. On resume, the existing disk-rehydration path is preserved. The two new tests (`SelectTaskWithPriorRun_LoadsStageStatusesFromDisk` and `RestartClearsStageBoard_DoesNotLoadStaleStatusFromDisk`) validate the clear-then-rehydrate infrastructure the fix relies on. Both targeted tests (31/31) and the full suite (2542 passed, 0 failed) exit clean."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "Confirmed the fix is in place and all tests pass. The fix in `MainWindowViewModel.RunOne.cs` line 14 conditionally calls `ResetStages(task.Id)` only on resume (reads disk to rehydrate prior progress) vs `ResetStages()` on restart (clears all stages to \"Waiting\" without reading stale disk state). Targeted tests (31/31) and full suite (2520/0) both exit clean."}

## Stage 9 - Verify

{
  "summary": "Fixed the bug where restarting a task (resume=false) showed multiple active green stages. Root cause: `RunOneAsync` unconditionally called `ResetStages(task.Id)`, which reads the prior run's `status.json` from disk and resurrected old \"Done\" entries as green. The fix in `MainWindowViewModel.RunOne.cs` line 14 makes the call conditional: `if (resume) ResetStages(task.Id); else ResetStages();`. On restart, `ResetStages()` clears all stages to \"Waiting\" without touching disk, so only the live `stage_start` event (via `ApplyStageEventToBoard`) sets the current stage green. On resume, the existing disk-rehydration path is preserved. After the run completes, `LoadRunHistoryAsync` reads the freshly-written `status.json` so final state is always accurate. All 2520 tests pass (0 failed, 22 skipped, total 2542).",
  "commitMessages": [
    "fix: conditionally skip reading stale status.json on task restart",
    "fix: restarting a task no longer shows multiple active stages from prior run",
    "fix: call ResetStages() without taskId on restart to avoid resurrecting old stage statuses",
    "fix: task restart clears stale stage statuses from previous run",
    "fix: prevent stale green stages on task restart"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

