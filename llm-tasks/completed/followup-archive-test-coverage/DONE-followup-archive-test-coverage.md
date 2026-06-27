# Add regression tests for the no-batch `completed/` archive branch

Follow-up from the code review of `move-completed-tasks-to-completed-subfolder` (commit
`6b4bd7b`). The production code is **correct** — these are pure test-coverage gaps the
review flagged as Important (regression guards), no behavior change required.

## Current state (researched)
`src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs` `RetireAsync` now archives a
completed task directly under `llm-tasks/completed/` when `archiveOnDone` is true and no
batch number resolves (flat → `completed/DONE-<id>.md`; nested → `completed/<id>/`).
`tests/VisualRelay.Tests/TaskCompletionArchiveNoBatchTests.cs` covers the happy paths +
idempotency, but two gaps remain:

1. **Rollback is untested for the new direct-under-`completed/` branch.** `RetireAsync`
   returns a rollback delegate that reverses the move if the git commit fails (flat:
   `File.Move(archivePath → donePath)`; nested: `Directory.Move(archivePath → taskDir)`).
   Verified correct by inspection but has no regression guard — a future edit to the
   rollback lambda could silently break it (commit-failure data loss).
2. **The flat no-batch `ListCompletedAsync` case is not directly tested.** The existing
   repository test uses the nested shape (`completed/<id>/DONE-<id>.md`). A flat archived
   task lives at `completed/DONE-<id>.md` (file directly in `completed/`, no subdir). It is
   covered end-to-end via the driver test, but `ListCompletedAsync` for the root-level file
   (→ `IsNested=false`, `ArchiveBatch=null`) has no targeted test.

## What to build (tests only)
1. **Rollback test(s)** in `TaskCompletionArchiveNoBatchTests.cs` (or alongside
   `RelayDriverGitCommitRetirementTests`): force the commit to fail (e.g. corrupt/lock the
   git repo, or invoke the returned rollback delegate directly) and assert the spec is
   restored to its original `llm-tasks/<id>/<id>.md` (or `<id>.md` flat) location and the
   `completed/` copy is gone. Cover BOTH flat and nested no-batch shapes.
2. **Flat `ListCompletedAsync` test:** place a task at `completed/DONE-<id>.md` (flat, no
   batch dir) and assert it appears in `ListCompletedAsync` with `StateLabel == "Completed"`
   and `ArchiveBatch == null`, and is absent from `ListAsync` (pending).
3. `./visual-relay check` green. No production-code changes expected; if a test reveals a
   real bug, fix it minimally and note it.

## Decisions (settled)
- Tests only — the reviewed code is correct. Keep changes confined to test files.
