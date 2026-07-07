## Stage 1 - Ideate

{
  "summary": "In TaskCompletionArchive.RetireAsync, when the retired task's id exists in config.SkipTestsTaskIds, call RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled: false) in a best-effort try/catch block (mirroring the existing FlaggedWorkStore.Delete pattern). This prunes stale entries from .relay/config.json on every retirement path (in-run sealed commit, manual Mark Done, mark-done command), without adding rollback complexity. The config change is automatically staged by GitCommitter's git add -u, landing cleanly in the archive commit. Tests verify the id is removed via RelayConfigLoader.TryLoadAsync, other ids survive, and non-skip-tested tasks cause no spurious rewrite.",
  "options": [
    "Option A (task-prescribed): Single best-effort call in RetireAsync. Add a guarded `if (config.SkipTestsTaskIds?.Contains(taskId, ...))` → `RelayConfigWriter.SetSkipTests(rootPath, taskId, false)` wrapped in try/catch, placed to run on all retirement returns except null. One line change in the production file. Minimal diff, keeps the file under 300 lines. Tests: (1) direct unit test mirroring TaskCompletionArchiveNoBatchRollbackTests.cs, (2) integration in RelayTaskRepositoryMarkDoneTests.cs, (3) in-run test mirroring RelayDriverGitCommitRetirementTests.cs asserting Committed status and clean HEAD with modified config.",
    "Option B: Extract a helper and call from all callers. Move the prune logic into a static helper `RelayConfigWriter.TryRemoveSkipTestId(rootPath, taskId)` and call it from each retirement entry point (the in-run commit, manual MarkDone, mark-done API handler) instead of from RetireAsync. This isolates the config write from RetireAsync's complexity but increases diff surface (touches 3+ call sites) and risks missing a future entry point. With RetireAsync as the documented single funnel, this is strictly worse for maintenance.",
    "Option C: Deferred cleanup via a separate maintenance command. Instead of pruning at retirement time, add a `relay prune-config` command (or lifecycle hook) that scans skipTestsTaskIds against all archived tasks and removes orphans in batch. Pro: separates concerns and avoids touching RetireAsync at all. Con: stale entries persist between retirements and the cleanup run; adds a new code path and CLI surface to test; contradicts the 'remove at retirement time' directive. Over-engineered for what is a simple one-liner prune."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is a C#/.NET project (VisualRelay). The task asks to prune stale `skipTestsTaskIds` entries from `.relay/config.json` when a task is retired. Key files identified:\n\n**Production file to modify:** `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs` (277 lines, under 300-line guard). The single retirement funnel `RetireAsync` handles all retirement paths: in-run sealed commit (via `RelayDriver.CommitGate.cs` line 151), manual \"Mark done\" button (via `RelayTaskRepository.MarkDone.cs` → `RetireAsync`), and `mark-done` control API (same path).\n\n**Config writer:** `RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled: false)` (`src/VisualRelay.Core/Init/RelayConfigWriter.cs`, line 194-234) does the read-modify-write to remove a taskId from the `skipTestsTaskIds` JSON array, preserving all other keys.\n\n**Namespace resolution:** From `VisualRelay.Core.Tasks`, the writer is accessible as `Init.RelayConfigWriter` without adding a `using` (C# sibling namespace lookup via `VisualRelay.Core.Init`). Same pattern as `Execution.FlaggedWorkStore.Delete` (line 22).\n\n**Existing best-effort pattern (line 22):** `try { Execution.FlaggedWorkStore.Delete(taskDirectory); } catch { /* best-effort */ }` — the prune call should mirror this exactly.\n\n**Git integration:** `.relay/config.json` is tracked (`.gitignore` has `.relay/*` then `!.relay/config.json`, and `RelayGitignoreWriter` writes `.relay/.gitignore` with `*`/`!config.json`). `GitCommitter`'s `git add -u` (line 99 of `GitCommitter.cs`) automatically stages any config.json changes made before the commit, so the prune lands in the archive commit and leaves a clean tree.\n\n**Config model:** `RelayConfig.SkipTestsTaskIds` (line 90 of `RelayConfig.cs`) is `IReadOnlyList<string>?` with default `[]`. `RelayConfigLoader.TryLoadAsync` (line 230 of `RelayConfigLoader.cs`) reads it via `OptionalStringArray(root, \"skipTestsTaskIds\", [])`.\n\n**Existing tests to follow:**\n- `TaskCompletionArchiveNoBatchRollbackTests.cs` (105 lines) — uses `MakeArchiveConfig()` helper, `TestRepository.Create()`, `repo.WriteTask()` / `repo.WriteNestedTask()`, asserts on file system state.\n- `RelayTaskRepositoryMarkDoneTests.cs` (94 lines) — uses `repo.WriteConfig(\"dotnet test\", [], archiveOnDone: true)`, creates `RelayTaskRepository`, calls `MarkDoneAsync`, asserts via `ListPendingAsync`/`ListCompletedAsync`.\n- `RelayDriverGitCommitRetirementTests.cs` (293 lines) — seeds repo with `TestGit.Run(\"git\", \"init\", ...)`, runs `RelayDriver` with `EditingSubagentRunner` + `ScriptedTestRunner`, asserts `RelayTaskOutcomeStatus.Committed` and git status via `git show --name-status --no-renames HEAD`.\n\n**Existing config writer tests:** `RelayConfigWriterSkipTestsTests.cs` shows the pattern for using `RelayConfigWriter.Write(repo.Root, \"dotnet test\")` + `SetSkipTests` + `RelayConfigLoader.TryLoadAsync` assertions.\n\n**Placement in RetireAsync:** The best-effort prune should be added after line 22 (the existing `FlaggedWorkStore.Delete` try/catch) but before any early returns. This ensures it runs on every retirement path (real moves and already-retired idempotent returns) but not on the `task is null` return (line 18). The guard `config.SkipTestsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true` prevents spurious rewrites for tasks never in the set.",
  "constraints": [
    "TaskCompletionArchive.cs must stay under 300 lines (currently 277); the single best-effort call approach preserves this",
    "Conventional Commits only — fixed type set, ≤72-char subject, lowercase after prefix, no trailing period, no em dashes, ≤3 `- ` body bullets ≤20 words each",
    "Plain xUnit `[Fact]` tests with `TestRepository` helper (Core-layer, no Avalonia)",
    "Minimal diffs: touch only `TaskCompletionArchive.cs` and the three test files",
    "Scope is `skipTestsTaskIds` only — do not touch `boostTurnsTaskIds` (`RelayConfigWriter.SetTurnBoost`)",
    "No rollback for config prune — best-effort try/catch only, mirroring the existing FlaggedWorkStore.Delete pattern",
    "Guard on membership before calling SetSkipTests to avoid spurious config rewrites for tasks never in the set",
    "The prune must run on all retirement paths (real moves AND already-retired idempotent returns), but not on the null (nothing to retire) return"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "In TaskCompletionArchive.RetireAsync (src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs, 277 lines), the single retirement funnel that handles all paths (in-run sealed commit via RelayDriver.CommitGate.cs line 151, manual MarkDone via RelayTaskRepository.MarkDone.cs, and mark-done control API), there is no code to remove the retired task's id from skipTestsTaskIds in .relay/config.json. The method already has a best-effort ancillary cleanup precedent at lines 20-22 (FlaggedWorkStore.Delete in try/catch), but no corresponding config prune. RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled: false) (src/VisualRelay.Core/Init/RelayConfigWriter.cs, lines 194-234) already exists and does the read-modify-write to remove a taskId from the array. GitCommitter.CommitAsync runs git add -u (src/VisualRelay.Core/Execution/GitCommitter.cs, line 99), which would automatically stage any config.json modification made before the commit. The config file is tracked (.gitignore has .relay/* then !.relay/config.json).",
  "excerpts": [
    "TaskCompletionArchive.cs:20-22 — best-effort pattern to mirror: try { Execution.FlaggedWorkStore.Delete(taskDirectory); } catch { /* best-effort */ }",
    "TaskCompletionArchive.cs:17-18 — null guard: if (task is null) return null; — prune must run after this, on all non-null returns",
    "RelayConfigWriter.cs:194-234 — SetSkipTests(rootPath, taskId, enabled: false) already removes from skipTestsTaskIds array via read-modify-write",
    "RelayConfig.cs:90 — SkipTestsTaskIds is IReadOnlyList<string>? (default null), loaded from JSON via OptionalStringArray at RelayConfigLoader.cs:230",
    "GitCommitter.cs:99 — var addTracked = await GitAsync(gi, rootPath, [\"add\", \"-u\"], cancellationToken); stages all tracked modifications including .relay/config.json",
    "RelayDriver.CommitGate.cs:151 — var retirement = TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task); — called before the git commit",
    "RelayTaskRepository.MarkDone.cs:19 — var retirement = TaskCompletionArchive.RetireAsync(RootPath, loaded.Config, task.Id, task); — manual MarkDone path through the same funnel"
  ],
  "repro": "1. Create a repo with .relay/config.json containing `\"skipTestsTaskIds\": [\"demo-task\"]` and a task file `llm-tasks/demo-task.md` with content `# Demo task\\n`. 2. Call TaskCompletionArchive.RetireAsync(rootPath, config, \"demo-task\", task) where config.SkipTestsTaskIds contains \"demo-task\". 3. Read .relay/config.json via RelayConfigLoader.TryLoadAsync — observe that \"demo-task\" is still present in SkipTestsTaskIds. Expected: it should be absent. The stale entry persists indefinitely because RetireAsync never prunes it."
}

## Stage 4 - Plan

{
  "plan": "## Production change (1 file)\n\n### `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`\n\nInsert a 4-line best-effort prune block immediately after the existing `FlaggedWorkStore.Delete` try/catch (after line 22, before line 24). The new block:\n\n```csharp\n        // Best-effort cleanup of skip-tests config entry for this task.\n        if (config.SkipTestsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true)\n        {\n            try { Init.RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled: false); } catch { /* best-effort */ }\n        }\n```\n\nThis mirrors the existing `FlaggedWorkStore.Delete` pattern exactly: `try { … } catch { }` with no rollback. The namespace `Init.RelayConfigWriter` resolves via C# sibling-namespace lookup from `VisualRelay.Core.Tasks` — no new using directive needed. The `Contains` guard prevents spurious config rewrites for tasks never in the skip-tests set. Placement after the null-guard (line 17-18) and before the source-gone check (line 24) ensures the prune runs on all non-null retirement returns: the two idempotent early returns (lines 31, 42), the two source+destination-exists returns (lines 94, 114), and the main retirement return (line 157). File grows from 277 to 281 lines, well under the 300-line ceiling.\n\n## Test files (3 files)\n\n### 1. `+tests/VisualRelay.Tests/TaskCompletionArchiveSkipTestsPruneTests.cs` (new)\n\nDirect unit tests mirroring the structure of `TaskCompletionArchiveNoBatchRollbackTests.cs`. Two [Fact] tests:\n\n- **`RetireAsync_PrunesSkipTestsTaskId`**: Create repo, write config with `RelayConfigWriter.Write` + `RelayConfigWriter.SetSkipTests(repo.Root, \"ship-status\", true)`, also add a second id `\"other-task\"` to the skip set. Write task `\"ship-status\"`. Construct `RelayTaskItem`. Call `TaskCompletionArchive.RetireAsync`. Assert via `RelayConfigLoader.TryLoadAsync` that `\"ship-status\"` is absent from `SkipTestsTaskIds`, `\"other-task\"` survives, and `TestCommand` is preserved.\n\n- **`RetireAsync_NotInSkipTestsSet_LeavesConfigUnchanged`**: Create repo, write config via `RelayConfigWriter.Write`, seed skipTestsTaskIds with `[\"other-task\"]` only. Write task `\"ship-status\"`. Record config.json last-write time before calling `RetireAsync`. After `RetireAsync`, assert the file's last-write time is unchanged (no spurious rewrite) and `\"other-task\"` is still in the loaded `SkipTestsTaskIds`.\n\n### 2. `tests/VisualRelay.Tests/RelayTaskRepositoryMarkDoneTests.cs` (add one test)\n\nAdd a `[Fact]` to the existing class:\n\n- **`MarkDoneAsync_PrunesSkipTestsTaskId`**: Create repo, `repo.WriteConfig(\"dotnet test\", [], archiveOnDone: true)`, call `RelayConfigWriter.SetSkipTests(repo.Root, \"windows-support\", true)`, write nested task `\"windows-support\"`. Create `RelayTaskRepository`, call `MarkDoneAsync`, then assert via `RelayConfigLoader.TryLoadAsync` that `\"windows-support\"` is absent from `SkipTestsTaskIds`.\n\n### 3. `tests/VisualRelay.Tests/RelayDriverGitCommitRetirementTests.cs` (add one test)\n\nAdd a `[Fact]` to the existing class mirroring the pattern of `RunTaskAsync_FlatTask_CommitContainsDeleteOfOldAndAddOfDone`:\n\n- **`RunTaskAsync_SkipTestsPruneLandsInCommit`**: Create repo, write config via `repo.WriteConfig(\"test -f src/status.cs\", [], archiveOnDone: false)`, then manually inject `\"skipTestsTaskIds\": [\"ship-status\"]` into `.relay/config.json`. Write task, init git, run `RelayDriver`. Assert `RelayTaskOutcomeStatus.Committed`. Load config via `RelayConfigLoader.TryLoadAsync` — assert `\"ship-status\"` is absent from `SkipTestsTaskIds`. Run `git show --name-status --no-renames --pretty=format: HEAD` — assert the output contains `M\\t.relay/config.json` (or `M\\t\".relay/config.json\"`), proving the prune landed in the sealed commit. Also assert the standard `D\\tllm-tasks/ship-status.md` and `A\\tllm-tasks/DONE-ship-status.md` lines appear.\n\n## Verification\n\n- `./visual-relay check` passes: Conventional Commits, 300-line guards, xUnit tests.\n- `TaskCompletionArchive.cs` stays at ~281 lines (from 277).",
  "manifest": [
    "src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs",
    "+tests/VisualRelay.Tests/TaskCompletionArchiveSkipTestsPruneTests.cs",
    "tests/VisualRelay.Tests/RelayTaskRepositoryMarkDoneTests.cs",
    "tests/VisualRelay.Tests/RelayDriverGitCommitRetirementTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/TaskCompletionArchiveSkipTestsPruneTests.cs",
    "tests/VisualRelay.Tests/RelayTaskRepositoryMarkDoneTests.cs",
    "tests/VisualRelay.Tests/RelayDriverGitCommitRetirementTests.cs"
  ],
  "rationale": "Production change (1 file, +6 lines, 283 total < 300): In TaskCompletionArchive.RetireAsync, added a best-effort prune block after the existing FlaggedWorkStore.Delete try/catch. Guards on config.SkipTestsTaskIds?.Contains(taskId, StringComparer.Ordinal) before calling Init.RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled: false) in try/catch. Runs on every non-null retirement return (real moves and idempotent already-retired), not on null. Mirrors the existing best-effort pattern exactly — no rollback.\n\nTests (3 files, 4 [Fact] methods):\n1. TaskCompletionArchiveSkipTestsPruneTests.cs (new) — RetireAsync_PrunesSkipTestsTaskId (two IDs seeded, retire one, assert it's gone + other survives + testCmd preserved) and RetireAsync_NotInSkipTestsSet_LeavesConfigUnchanged (timestamp guard, no spurious rewrite).\n2. RelayTaskRepositoryMarkDoneTests.cs — MarkDoneAsync_PrunesSkipTestsTaskId (WriteConfig + SetSkipTests → MarkDoneAsync → ID absent from loaded config).\n3. RelayDriverGitCommitRetirementTests.cs — RunTaskAsync_SkipTestsPruneLandsInCommit (asserts Committed, ID absent, M\t.relay/config.json in git show)."
}

## Stage 6 - Implement

{ "summary": "Implemented best-effort skipTestsTaskIds prune in TaskCompletionArchive.RetireAsync (6 lines, mirrors existing FlaggedWorkStore.Delete pattern). Compacted RelayDriverGitCommitRetirementTests.cs via SeedGitRepo helper extraction to stay under 300-line guard. All 2666 tests pass (0 failures)." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Production change (TaskCompletionArchive.cs, +6 lines, 283 total < 300): best-effort prune of skipTestsTaskIds in RetireAsync mirrors existing FlaggedWorkStore.Delete pattern. Tests (3 files, 4 new Facts): direct unit (prunes + preserves other ids, no spurious rewrite for non-member), MarkDone integration, in-run commit (asserts Committed + M\t.relay/config.json in git show). Full suite: 2666 passed, 0 failed, 22 skipped." }

## Stage 9 - Verify

{
  "summary": "Prune stale skipTestsTaskIds entries from .relay/config.json when a task is retired. A 6-line best-effort block in TaskCompletionArchive.RetireAsync (guarded on membership, wrapped in try/catch, no rollback) mirrors the existing FlaggedWorkStore.Delete pattern. Three test files (4 new [Fact] methods) cover direct unit, MarkDoneAsync integration, and in-run commit paths — all 2666 tests pass (0 failures).",
  "commitMessages": [
    "feat: prune skipTestsTaskIds on task retirement",
    "fix: remove archived task ids from config skipTestsTaskIds",
    "refactor: best-effort cleanup of skip-tests config in RetireAsync"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

