# Harness: reset worktree to HEAD after a flagged task before draining the next one

## Current state (researched)

### The contamination bug (observed 2026-06-14)

Task `surface-per-step-test-duration` introduced a hanging unit test. Stage 9 Verify
ran the full suite, hit the 10-minute `testTimeoutMs` wall, and `FlagAsync` was called.
The task was correctly marked `NEEDS-REVIEW` and `DrainAsync` continued to the next task
via the `ConsecutiveFlagThreshold` circuit breaker (`DrainCircuitBreaker.cs:8`,
`ConsecutiveFlagThreshold = 3`). But the flagged task's ~10 modified source files and the
untracked hanging test file were never reverted. Every subsequent task's stage 9 runs the
full suite, hits the same hanging test, times out, flags — and after 3 consecutive flags
the circuit breaker halts the entire batch.

### Why the success path leaves a clean tree

When a task commits successfully
(`RelayDriver.CommitGate.cs:159`), `GitCommitter.CommitAsync` runs:
1. `git reset -q` (unstages everything)
2. `git add -A` / `git add -u` (re-stages the manifest + tracked changes)
3. `git commit` (commits, leaving HEAD clean)

The commit makes the tree clean. The flag path (`RelayDriver.Events.cs:52–76`) does
none of this — it only writes `NEEDS-REVIEW` and emits an event.

### FlagAsync — all call sites that leave dirty trees

Every path below returns from `RunTaskAsync` via `FlagAsync` with worktree changes still
in place:

| Location | Trigger |
|---|---|
| `RelayDriver.cs:101` | Invalid subagent result (any stage) |
| `RelayDriver.cs:107–108` | Contract JSON parse failure (any stage) |
| `RelayDriver.cs:155` | Stage 5 author-test gate error |
| `RelayDriver.cs:160` | Stage 5 red-gate stash restore conflict |
| `RelayDriver.cs:166–167` | Stage 5 author-test timeout |
| `RelayDriver.cs:175–176` | Stage 5 author-tests passed after impl stripped |
| `RelayDriver.cs:199–200` | Stage 9 bootstrap smoke timeout |
| `RelayDriver.cs:215–216` | Stage 9 repo-guard timeout |
| `RelayDriver.cs:222–223` | Stage 9 full-suite timeout (**incident root cause**) |
| `RelayDriver.cs:255` | Stage 9 verify failed (MaxVerifyLoops ≤ 0) |
| `RelayDriver.cs:292` | Outer `catch (Exception)` |
| `RelayDriver.CommitGate.cs:163` | Stage 11 git commit failed |
| `RelayDriver.CommitGate.cs:175` | Stage 11 missing authored files |
| `RelayDriver.VerifyFix.cs:103–104` | Stage 10 invalid subagent result |
| `RelayDriver.VerifyFix.cs:122–123` | Stage 10 bootstrap timeout |
| `RelayDriver.VerifyFix.cs:144` | Stage 10 guard timeout |
| `RelayDriver.VerifyFix.cs:159` | Stage 10 test timeout |
| `RelayDriver.VerifyFix.cs:199–200` | Stage 10 all loops exhausted |

### Pre-run untracked snapshot — already captured and persisted

`RelayDriver.Snapshot.cs:44–69` calls `CapturePreRunUntrackedAsync` before any stage
runs. On a fresh (non-resume) run it calls `GitCommitter.CaptureUntrackedSnapshotAsync`
(`GitCommitter.cs:235–257`, `git ls-files --others --exclude-standard`) and persists the
result to `.relay/{taskId}/pre-run-untracked.txt`. This snapshot is the source of truth
for "which untracked files existed before this task ran."

`GitCommitter.FindUncommittedAuthoredFilesAsync` (`GitCommitter.cs:282–299`) already
does the diff: current untracked minus `preRunUntracked`, excluding `InternalArtifactPrefixes`
(`.relay/`, `.relay-scratch/`, `.swival/`) and `tasksDir` (`llm-tasks/` by default).
The same logic should drive deletion on flag.

### Existing stash/reset precedent in RedGate

`RedGate.RestoreStashAsync` (`RedGate.cs:85–111`) already runs:
```csharp
await GitAsync(rootPath, ["checkout", "--", "."], cancellationToken);  // revert tracked
await GitAsync(rootPath, ["stash", "apply", reference], cancellationToken);
```
`RedGate.StashAllAsync` (`RedGate.cs:113–121`) uses `git stash push -u -m tag` to
capture both tracked and untracked files. These patterns confirm `GitInvoker.RunAsync`
(`GitInvoker.cs:61`) is the established git invocation layer. The reset-on-flag
operation needs the same `git checkout -- .` for tracked files, plus targeted `git clean
-fd` for the authored untracked files (not a blanket `git clean -fdx` which would
destroy `.relay/` artifacts).

### Where to insert the reset — DrainAsync, not FlagAsync

`FlagAsync` is also called during single-task (non-drain) runs where auto-reset is
incorrect. The reset must be in `RelayQueueController.DrainAsync`
(`RelayQueueController.cs:217`), immediately after `RunTaskAsync` returns `Flagged`,
before `circuitBreaker.ShouldHalt` and `WriteNeedsReviewMarker`. The `tasksDir` comes
from `RelayConfigLoader.TryLoadAsync` (already called in Phase 1 at line 118).

## What to build

### 1. Add `WorktreeResetter` static helper

New file: `src/VisualRelay.Core/Execution/WorktreeResetter.cs`

```csharp
internal static class WorktreeResetter
{
    private static readonly string[] InternalArtifactPrefixes =
        [".relay/", ".relay-scratch/", ".swival/"];

    /// <summary>
    /// Resets the worktree to HEAD after a flagged task, leaving the next task
    /// with a clean slate.  Safe to call with any repo: no-ops on non-git roots.
    /// </summary>
    internal static async Task ResetAsync(
        string rootPath,
        string taskId,
        string? tasksDir,
        CancellationToken cancellationToken)
    {
        // 1. Revert all tracked modifications to HEAD.
        await GitAsync(rootPath, ["checkout", "--", "."], cancellationToken);

        // 2. Remove untracked files authored by this task (not pre-existing ones).
        var snapshotPath = Path.Combine(rootPath, ".relay", taskId, "pre-run-untracked.txt");
        var preRunUntracked = File.Exists(snapshotPath)
            ? await ReadSnapshotAsync(snapshotPath, cancellationToken)
            : new HashSet<string>(StringComparer.Ordinal);

        var currentUntracked = await CaptureUntrackedAsync(rootPath, cancellationToken);
        var toDelete = new List<string>();
        foreach (var path in currentUntracked)
        {
            if (!preRunUntracked.Contains(path)
                && !IsInternalArtifact(path)
                && !IsUnderTasksDir(rootPath, path, tasksDir))
            {
                toDelete.Add(path);
            }
        }

        foreach (var rel in toDelete)
        {
            var full = Path.Combine(rootPath, rel);
            if (File.Exists(full))
                File.Delete(full);
        }

        // Remove any directories that are now empty as a result.
        foreach (var dir in toDelete
            .Select(r => Path.GetDirectoryName(Path.Combine(rootPath, r)))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d!.Length))
        {
            if (dir is not null && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    private static async Task<IReadOnlySet<string>> ReadSnapshotAsync(
        string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines) { var t = l.Trim(); if (t.Length > 0) set.Add(t); }
        return set;
    }

    private static async Task<IReadOnlySet<string>> CaptureUntrackedAsync(
        string rootPath, CancellationToken ct)
    {
        var result = await GitAsync(rootPath, ["ls-files", "--others", "--exclude-standard"], ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return new HashSet<string>(StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in result.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = l.Trim(); if (t.Length > 0) set.Add(t);
        }
        return set;
    }

    private static bool IsInternalArtifact(string rel)
    {
        foreach (var pfx in InternalArtifactPrefixes)
            if (rel.StartsWith(pfx, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsUnderTasksDir(string rootPath, string rel, string? tasksDir)
    {
        if (string.IsNullOrEmpty(tasksDir)) return false;
        var full = Path.GetFullPath(Path.Combine(rootPath, rel));
        var dir  = Path.GetFullPath(Path.Combine(rootPath, tasksDir));
        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, dir, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output, bool TimedOut)> GitAsync(
        string rootPath, IEnumerable<string> args, CancellationToken ct) =>
        GitInvoker.RunAsync(rootPath, args, ct);
}
```

Notes:
- Do NOT use `git clean -fd` (can wipe harness scratch); prefer targeted `File.Delete`
  on the computed delta — same delta as `FindUncommittedAuthoredFilesAsync` at commit gate.
- `git checkout -- .` is used by `RedGate.RestoreStashAsync:102`; pattern is established.
- `pre-run-untracked.txt` is written by `CapturePreRunUntrackedAsync` before stage 1
  (`RelayDriver.cs:61`). If absent, `preRunUntracked` defaults to empty set → deletes
  every new untracked non-artifact file — still correct (conservative).
- `InternalArtifactPrefixes` must stay in sync with `GitCommitter.cs:9`.

### 2. Call `WorktreeResetter.ResetAsync` in `DrainAsync`

In `src/VisualRelay.Core/Queue/RelayQueueController.cs`, in the Phase 2 serial execute
loop, after `RunTaskAsync` returns and `outcome.Status == Flagged`, insert the reset call
**before** `WriteNeedsReviewMarker` and `circuitBreaker.ShouldHalt`:

```csharp
if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
{
    // Reset the worktree to HEAD so the next task starts clean.
    // Errors are intentionally swallowed: a reset failure must not
    // prevent the drain from continuing or the NEEDS-REVIEW marker
    // from being written.
    try
    {
        var tasksDir = (await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken)).Config?.TasksDir;
        await WorktreeResetter.ResetAsync(RootPath, task.Id, tasksDir, cancellationToken);
    }
    catch (Exception ex)
    {
        DrainSummaryLog.Write(RootPath, drainRunId, task.Id, "execute", "reset-failed", ex.Message);
    }

    WriteNeedsReviewMarker(outcome.TaskId, outcome.Reason ?? "Needs review");
    Tasks.Add(task with { ReviewReason = outcome.Reason ?? "Needs review" });
}
```

The `RelayConfigLoader.TryLoadAsync` call is cheap (small JSON file). If `DrainAsync`
already has a local `configResult` from the Phase 1 block, thread
`configResult.Config?.TasksDir` to avoid a second parse.

**Do NOT add the reset to `FlagAsync` itself.** Single-task runs and plan-phase
flagging should not auto-reset — only the queue drain has the ordering guarantee that
the next task is unrelated to the previous one.

Also apply the reset in the **Phase 1 parallel-plan flag path** (`DrainAsync` lines
147–178): call `WorktreeResetter.ResetAsync` inside the `Flagged or Failed` block before
`WriteNeedsReviewMarker`.

### 3. Tests (TDD — write these first)

All in `tests/VisualRelay.Tests/`.

**`WorktreeResetterTests.cs`** — unit tests against a real temp git repo:

- `ResetAsync_RevertsTrackedModifications`: create a tracked file, modify it, call
  `ResetAsync`, assert file is back to original content.
- `ResetAsync_RemovesAuthoredUntrackedFile`: write a pre-run snapshot (empty), create
  a new untracked file, call `ResetAsync`, assert the new file is deleted.
- `ResetAsync_PreservesPreExistingUntrackedFiles`: write a pre-run snapshot containing
  an untracked file, call `ResetAsync`, assert the pre-existing file is NOT deleted.
- `ResetAsync_PreservesRelayArtifacts`: create `.relay/some-task/NEEDS-REVIEW` after
  run start; call `ResetAsync`; assert `.relay/` subtree is untouched.
- `ResetAsync_PreservesTasksDirFiles`: create a file under `llm-tasks/`; call `ResetAsync`;
  assert the tasks-dir file survives.
- `ResetAsync_MissingPreRunSnapshot_FallsBackToEmptySet`: omit `pre-run-untracked.txt`;
  create a new untracked file; call `ResetAsync`; assert the new file is deleted.
- `ResetAsync_NonGitRepo_DoesNotThrow`: call on a plain temp directory; assert no exception.

**`RelayQueueControllerTests.WorktreeReset.cs`** — integration test:

- `DrainAsync_FlaggedTask_ResetsWorktreeBeforeNextTask`: construct a
  `RelayQueueController` with a scripted runner. The first task writes a tracked
  modification (or creates an untracked file) then returns `Flagged`. The second task
  asserts the tree is clean during execution. Assert both ran, the second task's check
  did not fail, and the first task has `NeedsReview = true`.

  Use a real git temp repo (`TestRepository.Create()`); this test exercises actual
  filesystem state so a real repo is preferred over mocked git calls.

## Done when

- **Write failing tests first** before any source changes.
- `WorktreeResetterTests` are green: tracked reverts, untracked removals, artifact
  preservation, missing-snapshot fallback, non-git no-throw.
- `DrainAsync_FlaggedTask_ResetsWorktreeBeforeNextTask` passes: the second task in a
  drain starts with a clean worktree after the first task flags with dirty changes.
- The hanging-test incident is closed: a task that authors a hanging test file and flags
  on Verify timeout leaves no trace in the tree; the next task's Verify runs a clean suite.
- `WorktreeResetter.ResetAsync` is a no-op on an already-clean tree (idempotent).
- A reset failure is logged to `DrainSummaryLog` but does NOT abort the drain or
  prevent `NEEDS-REVIEW` from being written.
- `WorktreeResetter.cs` is under 120 lines; `RelayQueueController.cs` change is a small
  try/catch block under 15 lines; no single file exceeds 300 lines.
- **Relationship to adjacent tasks:**
  - `DONE-drain-continue-past-flagged-tasks`: continues past flags but doesn't clean the
    tree. This task adds the cleanup that makes continued draining safe.
  - `harness-drain-resilient-to-task-crash`: guards `DrainAsync` against unhandled
    exceptions from `RunTaskAsync`. The worktree reset sits at the same call site but is
    orthogonal — it runs on `Flagged` outcomes (not exceptions), after the exception guard.
    Both changes can coexist at the same `RunTaskAsync` call site.
- **`./visual-relay check` is green** — all pre-existing tests pass unmodified.
- **Conventional Commit** subject candidates:
  - `fix(drain): reset worktree to HEAD after flagged task before draining next`
  - `fix(driver): clean authored changes from tree on flag to prevent queue contamination`
