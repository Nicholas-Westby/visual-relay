# Drop Archived Tasks From `skipTestsTaskIds`

When a task marked "Skip automated testing" is completed/archived, its id is left
behind in the `skipTestsTaskIds` array of `.relay/config.json`. A retired task
never runs again, so the entry is dead weight. Remove it at retirement time so the
config reflects reality.

## Current state (researched)

- The skip-tests set is the `skipTestsTaskIds` JSON array. `RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled)` (`src/VisualRelay.Core/Init/RelayConfigWriter.cs`) read-modify-writes it — de-dupes on add, removes on disable, preserves all other keys. The in-memory field is `RelayConfig.SkipTestsTaskIds` (`src/VisualRelay.Domain/RelayConfig.cs`).
- Every task retirement funnels through one method: `TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task)` (`src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`). Both paths call it: the in-run sealed commit (`RelayDriver.CommitGate.cs`, `ExecuteCommitStageAsync` → `TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task)`, invoked *before* the git commit) and the manual "Mark done" button / `mark-done` control-API command (`MainWindowViewModel.MarkDone.cs` `MarkSelectedTaskDoneAsync` → `RelayTaskRepository.MarkDoneAsync` → `RetireAsync`). (`archive-toggle` is only the archive *view* toggle — not a retirement path.)
- `RetireAsync` already does best-effort ancillary cleanup that is **not** rolled back: `try { FlaggedWorkStore.Delete(taskDirectory); } catch { }` near the top of the method. This is the precedent to mirror.
- `.relay/config.json` is tracked, not gitignored: the repo `.gitignore` has `.relay/*` then `!.relay/config.json`, and `RelayGitignoreWriter` writes `.relay/.gitignore` with `*` / `!config.json` for target repos. So in a sealed run, `GitCommitter`'s `git add -u` (`src/VisualRelay.Core/Execution/GitCommitter.cs`) stages any config.json change made before the commit — it lands in the archive commit and leaves a clean tree on success.

## What to build (TDD-first)

1. **Prune in `RetireAsync`.** In `TaskCompletionArchive.RetireAsync`, when the retired task's id is in the set (`config.SkipTestsTaskIds?.Contains(taskId, StringComparer.Ordinal) == true`), call `RelayConfigWriter.SetSkipTests(rootPath, taskId, enabled: false)` to drop it. Wrap in `try { … } catch { }` as best-effort, mirroring the existing `FlaggedWorkStore.Delete` call — do **not** add it to the rollback delegates. Guard on membership so a task that never opted into skip-tests triggers no spurious config rewrite. Place the call so it runs on every retirement (the real-move paths and the already-retired idempotent returns), not on the "nothing to retire" null return.

   This is final: best-effort, no rollback. On the rare in-run commit-failure rollback the task is restored to runnable but loses its skip-tests flag (re-togglable by the user); the edit self-heals into the next successful commit. This keeps `RetireAsync` under the 300-line guard instead of threading config-restore through every return path.

2. **Tests** — plain xUnit `[Fact]` + the `TestRepository` helper (Core-layer, no Avalonia):
   - Direct unit test mirroring `tests/VisualRelay.Tests/TaskCompletionArchiveNoBatchRollbackTests.cs`: seed `.relay/config.json` with the task's id in `skipTestsTaskIds` (e.g. `RelayConfigWriter.Write` then `RelayConfigWriter.SetSkipTests(root, "ship-status", true)`), pass `RetireAsync` a `RelayConfig` whose `SkipTestsTaskIds` contains the id (extend the file's `MakeArchiveConfig()` helper), retire, then assert via `RelayConfigLoader.TryLoadAsync` that the id is gone while `testCmd` and any *other* skip-tests id survive. Add a case: a task not in the set leaves config.json unchanged (no spurious rewrite).
   - Integration test in `tests/VisualRelay.Tests/RelayTaskRepositoryMarkDoneTests.cs`: after `MarkDoneAsync`, the retired task's id is absent from the loaded `SkipTestsTaskIds`.
   - In-run test mirroring `tests/VisualRelay.Tests/RelayDriverGitCommitRetirementTests.cs`: a completing run seeded with `"skipTestsTaskIds": ["<taskId>"]` reaches `RelayTaskOutcomeStatus.Committed`, the loaded config no longer lists the id, and `.relay/config.json` shows as modified in `HEAD` (`git show --name-status HEAD`) — proving the prune landed in the sealed commit, not left dirty.

## Done when

- A task whose id is in `skipTestsTaskIds`, once retired — via the in-run sealed commit or the manual "Mark done" button / `mark-done` command — no longer appears in `.relay/config.json`'s `skipTestsTaskIds`.
- Other config keys and other tasks' skip-tests ids are preserved.
- A task never in the set causes no config rewrite on retirement.
- The in-run archive commit includes the config change (clean tree on success).
- `./visual-relay check` passes.

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset: fixed type set, ≤72-char subject, lowercase after prefix, no trailing period, no em dashes, ≤3 `- ` body bullets ≤20 words each). See `docs/commit-messages.md`, `AGENTS.md`.
- C# and Avalonia XAML source files must stay under 300 lines (`tools/VisualRelay.Guards`, run by `./visual-relay check`). `TaskCompletionArchive.cs` is currently 277 lines — the best-effort single-call approach keeps it under the ceiling; do not thread rollback logic through every return.
- Plain logic tests use xUnit `[Fact]` with the `TestRepository` helper, matching the existing retirement tests.
- Minimal diffs: touch only `TaskCompletionArchive.cs` and the test files. Do not reformat unrelated code.
- Scope is `skipTestsTaskIds` only. The parallel `boostTurnsTaskIds` array (`RelayConfigWriter.SetTurnBoost`) has the same staleness property but is out of scope — do not change it.
