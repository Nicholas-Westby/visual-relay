# Task: Protect pending task specs from the pipeline — review-scope carve-out, empty-input hard fail, reset logging

## The incident this prevents (proven, 2026-07-12)

A spec folder (`llm-tasks/09-new-task-templates/`, one 37 KB markdown file) was created
while a Run-All drain was mid-flight, so it was untracked (the drain-start "chore: add
tasks" sweep predated it). What followed, each step with an artifact (raw run artifacts
preserved in git history under commit `0aa6b9c`; deleted from the working tree by the
revert `c455d31`):

1. The **Review stage (7)** of the *unrelated task running at the time* diffed the tree
   and filed a finding: *"Untracked stray content: llm-tasks/09-new-task-templates/
   (09-new-task-templates.md, ~37KB) is unrelated to this task and should not be part of
   this diff; it appears to be accidental or leftover from another task."*
2. That task's **Fix stage (9)** acted on the finding. Its own summary: *"Scope
   violations resolved: … removed stray `llm-tasks/09-new-task-templates/`."* The pending
   spec was deleted from disk.
3. 0.24 s after that task committed, the drain started the now-folderless task from its
   in-memory queue (the GUI had pushed it via `SetExternalTaskSource`/`SyncExternalTasks`
   when it was created; the controller never re-checks disk).
4. `RelayDriver.RunAsync` re-listed tasks from disk, found nothing — and **silently
   substituted an empty input**. The run log shows `s1/cheap stage_input systemBytes=64
   inputBytes=294` (prompt boilerplate only); the stage-1 prompt contains `## Task input`
   followed by nothing.
5. Eight stages ran against the empty input, fabricated a plausible task from the folder
   slug alone, passed verify green, and committed invented code. Retirement no-op'd (null
   task), so no archive entry existed either — the failure was invisible everywhere
   except the commit itself.

Four fixes below. All tasks-directory references must come from `RelayConfig.TasksDir` —
never hardcode `llm-tasks` (Visual Relay runs on arbitrary repos).

## Fix 1 — Teach Review and Fix that the tasks dir is out of scope

The pipeline already encodes "tasks dir = queue bookkeeping, not code" everywhere
mechanical: `RelayDriver.CodeChangeGate.cs` (`IsBookkeepingPath` ends with
`return IsPathUnderDirectory(rootPath, path, tasksDir);`), `WorktreeFilter.cs`,
`WorktreeResetter.cs`, and `GitCommitter.Untracked.cs` (all via `IsUnderTasksDir`). The
Review/Fix **prompts** are the only place the policy is missing — and those agents run
their own `git status`, so the fix must be prompt-level plus a concrete path in the
stage input.

### 1a. Concrete "Protected paths" line in every stage prompt

- `src/VisualRelay.Domain/StageInvocation.cs` (37 lines): add a trailing optional
  parameter after `MaxSelfEscalations`:

```csharp
    // Repo-relative tasks directory (RelayConfig.TasksDir). When set, BuildPrompt
    // emits a "Protected paths" header line naming it as queue bookkeeping that is
    // never part of the task's diff. Null (default) omits the line.
    string? TasksDir = null);
```

- `src/VisualRelay.Core/Execution/ProcessRunners.Prompt.cs` → `BuildPrompt` (106 lines):
  the header lines live in the initial `parts` list literal
  (`$"Working directory: {invocation.TargetRoot}",` is index 2). Immediately after the
  literal, insert:

```csharp
        if (!string.IsNullOrWhiteSpace(invocation.TasksDir))
        {
            // Right after "Working directory:" so every stage sees it before the task input.
            parts.Insert(3, $"Protected paths (queue bookkeeping — never part of this task's diff): {invocation.TasksDir}/, .relay/, .relay-scratch/, .swival/");
        }
```

- `src/VisualRelay.Core/Execution/RelayDriver.Invocation.cs` → `BuildInvocation`
  (116 lines) already receives `RelayConfig config`; add `TasksDir: config.TasksDir,` to
  its `new StageInvocation(...)` named arguments (one line). Leave the other two
  construction sites (`FixTaskAuthorRunner.cs`, `TaskRewriteRunner.cs`) unset — null
  omits the line and changes nothing there.

### 1b. Policy sentences in the Review and Fix system prompts

`src/VisualRelay.Core/Execution/RelayStages.cs` (123 lines). The `"Review"` case
currently ends with `"Do not edit files."`. Append one sentence so it ends:

```
Do not edit files. Paths named on the 'Protected paths' line of your input (the tasks
directory and Visual Relay's internal artifact dirs) are queue bookkeeping, NEVER part
of the diff under review — even when untracked: pending specs for OTHER tasks
legitimately appear there mid-drain, so never flag them as stray content.
```

The `"Fix"` case currently ends with `"…do NOT reformat, reflow, or compact unrelated
code to satisfy size or style budgets."`. Append:

```
Never delete, edit, or revert anything under the paths named on the 'Protected paths'
line of your input — pending specs for other tasks legitimately sit there untracked
mid-drain. If review flagged such a path, record it in your summary as
skipped-by-policy instead of acting on it.
```

(Match the existing string-concatenation style of the file; keep each case a single
concatenated literal.)

## Fix 2 — Hard-fail a run whose task input is missing or empty

`src/VisualRelay.Core/Execution/RelayDriver.cs` is at **299/300 lines** — this fix must
add exactly one line there. Current code (the defect):

```csharp
            var task = (await repository.ListAsync(includeNeedsReview: true, cancellationToken)).FirstOrDefault(x => x.Id == taskId);
            var input = task is null ? new RelayTaskInput(string.Empty, null) : await repository.ReadTaskInputAsync(task, cancellationToken);
```

Keep those two lines, and insert exactly one line immediately after the
`ValidateCommitGateResumeAsync` early return
(`if (commitGateOutcome is not null) return commitGateOutcome;`), before the
`isReAdded` line:

```csharp
            if (await FailIfTaskInputMissingAsync(task, input, rootPath, runId, taskId, taskDirectory, ledger, statusEntries, cancellationToken) is { } emptyInputOutcome) return emptyInputOutcome;
```

That takes the file to exactly 300 — at the guard limit, which passes; nothing else may
be added to this file.

`FailIfTaskInputMissingAsync` lives in a **new partial**
`src/VisualRelay.Core/Execution/RelayDriver.TaskInputGate.cs` (keep ≤ 300 lines; mirror
the file-header comment style of `RelayDriver.Invocation.cs`). Behavior:

- Returns `null` (no-op) when `task` is not null AND
  `!string.IsNullOrWhiteSpace(input.Markdown)`.
- Otherwise the run must fail before any stage executes:
  1. Write `.relay/<taskId>/NEEDS-REVIEW` whose first line is exactly
     `task spec missing or empty at run start — refusing to run stages against an empty input`
     (the repository already surfaces the first line as `ReviewReason`, and
     `RelayDriver.RunAsync` clears any stale marker at start via
     `File.Delete(Path.Combine(taskDirectory, "NEEDS-REVIEW"))`, so writing here is
     safe and the card shows "Needs review" with that reason).
  2. Publish an `error`-level `RelayEvent` (name `empty_task_input`) through
     `_dependencies.EventSink`, mirroring the `run_start` publish a few lines below so
     the drain log records why nothing ran.
  3. Persist status and return a failed outcome. Mirror the early-return construction
     used by `RestoreFlaggedWorkIfNeededAsync` (called two lines below the insertion
     point; it returns a nullable outcome exactly this way) — reuse whatever
     status-write + outcome shape that path uses for "run did not proceed".
- The gate sits after resume-state loading on purpose: a *resumed* task whose folder
  vanished must fail the same way.

Tests — new standalone `tests/VisualRelay.Tests/RelayDriverEmptyTaskInputTests.cs`
(sealed class; mirror the harness of the existing small driver-test classes, e.g.
`RelayDriverBootstrapTests.cs`, on the GitSim/`TestRepository` pattern):

- `Run_TaskFolderMissing_FailsWithNeedsReview` — drive the driver at a `taskId` whose
  folder does not exist: outcome is failed, `.relay/<id>/NEEDS-REVIEW` exists and its
  first line contains `task spec missing or empty`, no `stage1-attempt1.input.json` was
  written, and no commit was created.
- `Run_TaskMarkdownWhitespaceOnly_FailsWithNeedsReview` — folder exists,
  `<id>/<id>.md` contains only `"\n\n"`: same assertions.
- `Run_NormalTask_GateIsNoOp` — a normal task still reaches stage 1 (assert
  `stage1-attempt1.input.json` exists or the run proceeds past the gate; keep this fact
  cheap — it exists to pin that the gate never fires on healthy input).

## Fix 3 — Plan-phase reset must never see a null tasks dir

`src/VisualRelay.Core/Queue/RelayQueueController.cs` is at **300/300 lines** — this fix
must be net-zero. Line 184 (plan phase) passes a null-conditional with no fallback:

```csharp
                                    await ResetAndLogAsync(taskId, configResult?.Config?.TasksDir, drainRunId, "plan", drainCts.Token);
```

while the execute phase (lines 263-265) already defends:

```csharp
                    var tasksDir = configResult?.Config?.TasksDir
                        ?? (await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken)).Config.TasksDir;
```

A null `tasksDir` disables `WorktreeResetter`'s `IsUnderTasksDir` exemption, letting a
plan-phase reset delete untracked task specs in Standard-mode drains — the same
destruction as the incident, via a second door. Edit line 184 **in place** (extend the
same line, adding no lines) to apply the identical fallback:

```csharp
await ResetAndLogAsync(taskId, configResult?.Config?.TasksDir ?? (await RelayConfigLoader.TryLoadAsync(RootPath, drainCts.Token)).Config.TasksDir, drainRunId, "plan", drainCts.Token);
```

No new test: whenever a config loads (always, in the harnesses) the expression is
behavior-identical; the change is compile-checked and covered by the existing
plan-phase suites. Do not restructure the method to "clean this up" — the file has zero
line budget.

## Fix 4 — WorktreeResetter must say what it deleted

`WorktreeResetter.ResetAsync` (130 lines) deletes untracked files with no logging — the
incident investigation had to rule it out by reading code, not logs. Change its return
type from `Task` to `Task<IReadOnlyList<string>>`, returning `toDelete` (the
repo-relative paths actually removed; return an empty list on the no-op paths).

In `RelayQueueController.PrivateHelpers.cs` → `ResetAndLogAsync` (121 lines), log a
drain-summary line when anything was removed:

```csharp
        try
        {
            var removed = await WorktreeResetter.ResetAsync(RootPath, taskId, tasksDir, ct, gi);
            if (removed.Count > 0)
            {
                var sample = string.Join(", ", removed.Take(5));
                DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase,
                    "reset-removed", $"{removed.Count} untracked file(s): {sample}{(removed.Count > 5 ? ", …" : "")}");
            }
        }
        catch (Exception ex) { DrainSummaryLog.Write(RootPath, drainRunId, taskId, phase, "reset-failed", ex.Message); }
```

Tests:

- `tests/VisualRelay.Tests/WorktreeResetterTests.cs` (250/300, sealed, GitSim-backed):
  update existing call sites for the new return type, and add one fact
  `ResetAsync_ReturnsDeletedPaths_AndSparesTasksDir` — a stray untracked file is
  deleted and appears in the returned list; an untracked file under the tasks dir
  survives on disk and is absent from the list. If the additions push the file past
  300, move the new fact to a new standalone sealed class
  `WorktreeResetterDeletionListTests.cs` instead of trimming assertions.
- `tests/VisualRelay.Tests/RelayQueueControllerTests.WorktreeReset.cs` (117/300): add
  one fact asserting that after a reset which removed at least one file, the drain log
  contains a `reset-removed` line naming the count (read the `drain-*.log` the harness
  already uses for `reset-failed`-style assertions; mirror whichever log-reading idiom
  that file already has).

## Verification

1. `dotnet build` — clean.
2. `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter "FullyQualifiedName~RelayDriverEmptyTaskInputTests|FullyQualifiedName~WorktreeResetterTests|FullyQualifiedName~RelayQueueControllerTests"` — green.
3. Full suite green.
4. `./visual-relay check` passes; the diff introduces zero new InspectCode findings (any
   best-effort catch keeps a rationale comment inside the block).

Line budgets (guard limit 300; verify with `wc -l` after editing):

| File | Before | After |
|---|---|---|
| `RelayDriver.cs` | 299 | **exactly 300** (one inserted line, nothing else) |
| `RelayQueueController.cs` | 300 | **300** (line 184 edited in place) |
| `RelayStages.cs` | 123 | ≤ 140 |
| `ProcessRunners.Prompt.cs` | 106 | ≤ 115 |
| `StageInvocation.cs` | 37 | ≤ 45 |
| `RelayDriver.Invocation.cs` | 116 | ≤ 118 |
| `RelayDriver.TaskInputGate.cs` | new | ≤ 300 |
| `RelayQueueController.PrivateHelpers.cs` | 121 | ≤ 132 |
| `WorktreeResetter.cs` | 130 | ≤ 140 |

## Rejected approaches (do not do these)

- **Sandbox-denying writes under the tasks dir** — pipeline agents legitimately author
  new task specs there (that is a designed workflow); the fix is scope policy, not a
  write ban.
- **A controller-side disk re-check before executing each queued task** — redundant
  once the driver hard-fails; the driver gate is the single authoritative check and
  covers every entry point (GUI single-run, control API, drain).
- **Filtering the diff the review agent sees mechanically** — the review agent runs its
  own `git status`/`git diff`; no driver-side filter can hide paths from it. The
  concrete `Protected paths` prompt line plus the policy sentences are the mechanism.
- **Auto-committing pending specs mid-drain** so they stop being untracked — changes
  the git semantics of the user's tree out from under them; the drain-start sweep
  already handles specs that exist when the drain begins.
- **Threading `TasksDir` into `FixTaskAuthorRunner`/`TaskRewriteRunner` invocations** —
  out of scope; those flows don't review diffs.
- **Publishing only an event (no NEEDS-REVIEW file) on empty input** — events scroll
  away; the marker file is what the queue UI surfaces as a red "Needs review" card with
  a reason, which is the visibility the incident lacked.

## Constraints

- Touch only: `RelayStages.cs`, `ProcessRunners.Prompt.cs`, `StageInvocation.cs`,
  `RelayDriver.cs` (one line), `RelayDriver.Invocation.cs` (one line),
  `RelayDriver.TaskInputGate.cs` (new), `RelayQueueController.cs` (one line in place),
  `RelayQueueController.PrivateHelpers.cs`, `WorktreeResetter.cs`, and the test files
  named above.
- Do not modify `RelayDriver.CodeChangeGate.cs`, `WorktreeFilter.cs`,
  `GitCommitter.Untracked.cs`, or any archive/retirement code — their tasks-dir
  handling is already correct.
- No behavior change for runs with healthy task input; no new config keys.
- Conventional Commits (hyphen-bullet body, ≤3 bullets, no path-like tokens in
  bullets); minimal diffs.
