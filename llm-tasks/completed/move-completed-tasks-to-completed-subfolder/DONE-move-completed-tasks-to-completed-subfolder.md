# Move completed tasks into `llm-tasks/completed/`

When a task completes, its spec should be **moved into `llm-tasks/completed/`** rather than left in
place. Today completed tasks pile up where they were authored — top-level `DONE-<id>.md` files and
`<folder>/DONE-<id>.md` inside their original subfolders — cluttering the tasks directory. They
should land under `completed/` so the active tasks dir only holds open work.

## Current state (researched)

> **Freshness contract.** Verify each reference by searching for the quoted string, not by line
> number; re-read the file if a snippet has drifted.

**Retirement entry point (the only one).** `src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`
calls it once, inside the `_options.CreateGitCommit` branch:

```csharp
var retirement = TaskCompletionArchive.RetireAsync(rootPath, config, taskId, task);
```

`retirement.Additions` are staged into the same commit; `retirement.Rollback` runs if the commit
fails. So the move must happen here, before the commit, and report its new paths.

**The retirement logic.** `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs` `RetireAsync`:

- **Step 1 (always):** `File.Move(task.MarkdownPath, doneFilePath)` where
  `doneFilePath = Path.Combine(task.TaskDirectory, $"DONE-{task.Id}.md")` — renames `<id>.md` →
  `DONE-<id>.md` **in the task's own folder**.
- **Step 2 (move to `completed/`):** an `archivePath` is computed **only when**
  `config.ArchiveOnDone` is true **and a batch number is found**:

  ```csharp
  if (config.ArchiveOnDone) { batch = ReadBatchNumber(File.ReadAllText(task.MarkdownPath)); }
  …
  if (config.ArchiveOnDone)
  {
      batch ??= HighestCompletedBatch(rootPath, config.TasksDir);
      if (batch is not null)
      {
          var batchDir = Path.Combine(rootPath, config.TasksDir, "completed", $"batch-{batch}");
          archivePath = task.IsNested
              ? Path.Combine(batchDir, task.Id)
              : Path.Combine(batchDir, $"DONE-{task.Id}.md");
      }
  }
  ```

  `batch` comes from a `batch: N` line in the markdown (`ReadBatchNumber`, regex
  `^batch:[ \t]*(\d+)\s*$`) or from `HighestCompletedBatch` (scans existing `completed/batch-N`
  dirs).

**The bug this task fixes.** In this repo **no spec has a `batch:` line** and `completed/` does
not exist, so `batch` is always null ⇒ `archivePath` stays null ⇒ **Step 2 is skipped** ⇒ completed
tasks remain `DONE-<id>.md` in place. Meanwhile `ArchiveOnDone` defaults **true**
(`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`) and is **true** in this repo's
`.relay/config.json`. So the intent is already "archive on done" — it just silently no-ops without
a batch number.

**The archive view already supports direct-under-`completed/` placement.**
`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs` `ListCompletedAsync` scans
`completed/**/DONE-*.md` with `SearchOption.AllDirectories`; `FindBatchName` returns `null` unless
the first path segment starts with `batch-`; `ArchivedTaskFromPath` handles `batchName is null`
(`batchDirectory = completedRoot`, `IsNested` derived from the file's directory). So both
`completed/DONE-<id>.md` (flat) and `completed/<id>/DONE-<id>.md` (nested) render correctly with
`ArchiveBatch: null`. **No archive-view change is needed.**

**Discovery already skips `completed/`.** `RelayTaskRepository` has
`SkippedDirectories = ["completed", "_ideation"]`, so moved tasks won't reappear in the pending
list. **No discovery change is needed.**

**Idempotency helpers currently only look *inside* batch dirs.** `FindExistingArchivedPath`
iterates `Directory.EnumerateDirectories(completedDir)` and checks `<batchDir>/<id>` (nested) or
`<batchDir>/DONE-<id>.md` (flat). It does **not** check direct-under-`completed/` locations — it
must learn them once we place tasks there.

## What to build

All production changes live in **`src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`** (the
single retirement seam).

1. **Always archive under `completed/` when `archiveOnDone` is true** — never leave `archivePath`
   null in that case:
   - **batch known** (markdown `batch: N` or `HighestCompletedBatch`) → `completed/batch-{batch}/…`
     (**unchanged**).
   - **batch unknown** → directly under `completed/`:
     - nested task → `Path.Combine(completedRoot, task.Id)` (the whole folder is `Directory.Move`d,
       attachments included)
     - flat task → `Path.Combine(completedRoot, $"DONE-{task.Id}.md")`

     where `completedRoot = Path.Combine(rootPath, config.TasksDir, "completed")`.

   The existing Step-2 move (`Directory.CreateDirectory(Path.GetDirectoryName(archivePath)); …
   Directory.Move / File.Move`) then performs the move and creates `completed/` as needed.

2. **Teach idempotency about the direct-under-`completed/` destinations** so re-running a completed
   task doesn't throw or double-move:
   - `FindExistingArchivedPath`: also check `completed/<id>` (nested) and `completed/DONE-<id>.md`
     (flat) directly, in addition to inside batch dirs.
   - The inline "destination already exists" early-return already works once `archivePath` is the
     direct location.

3. **Keep honoring `archiveOnDone`.** If it is false, retain today's behavior (DONE- rename in
   place, no move). Do not remove the config option.

4. **Leave `ListCompletedAsync`, discovery, `CollectAdditions`, and the rollback delegate as-is** —
   they already handle nested moves and direct-under-`completed/` reads. Verify the rollback still
   reverses the direct case (`Directory.Move(archivePath → taskDir)` for nested,
   `File.Move(archivePath → donePath)` for flat).

**Do NOT migrate** the existing legacy `DONE-*.md` files already scattered in `llm-tasks/`
(top-level + folders). They still appear in the Archive view, and a bulk rename would be a huge
noisy commit. Only *new* completions move to `completed/`. (Worth a one-line note in the PR.)

## Tests / verification (TDD — write the failing test first)

- **Driver test** (mirror `tests/VisualRelay.Tests/RelayDriverGitCommitTests.cs`, whose existing
  cases all use `batch: N`): complete a task with **no `batch:` line** and `archiveOnDone: true` →
  assert it now lives under `llm-tasks/completed/…` (`completed/DONE-<id>.md` flat, or
  `completed/<id>/DONE-<id>.md` nested) and the original path no longer exists. The existing
  `batch: N` tests must still pass (→ `completed/batch-N/…`).
- **Repository test**: a task at `completed/<id>/DONE-<id>.md` (no batch dir) appears in
  `ListCompletedAsync` with `StateLabel == "Completed"` and `ArchiveBatch == null`, and is **absent**
  from the pending list (`ListAsync`).
- **Idempotency test**: retiring/re-running the same completed task twice neither duplicates nor
  throws (the second pass detects the existing archived destination via `FindExistingArchivedPath`).
- Search for and update any test asserting a no-batch completion *stays in place* (invert it). The
  current commit tests all use `batch: N`, so there may be none — but verify.
- `./visual-relay check` green.

## Decisions (settled)

1. **Fix `RetireAsync` to always archive under `completed/` when `archiveOnDone` is true**,
   defaulting to directly-under-`completed/` when no batch number exists. *Why:* `archiveOnDone=true`
   already promises "archive on done"; the batch-gating silently breaks that promise — which is
   exactly what the user is asking to fix.
2. **Reuse the existing `completed/batch-N/` infra for batched tasks; only ADD the no-batch
   fallback.** *Why:* minimal, low-risk; batched workflows stay identical.
3. **No archive-view or discovery changes.** *Why:* they already render direct-under-`completed/`
   tasks and skip `completed/` during discovery.
4. **No migration of legacy in-place `DONE-` files.** *Why:* out of scope; avoids a massive noisy
   rename commit, and the Archive view shows them regardless.

## Notes

- Single production file: `src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`. Entry point:
  `RelayDriver.CommitGate.cs` (`TaskCompletionArchive.RetireAsync`).
- Context history (read if needed): `DONE-commit-task-retirement-with-the-run`,
  `DONE-completed-tasks-missing-from-archive`, `DONE-folder-task-done-residue-resurrects-as-pending`
  — these explain why retirement is staged into the run's commit and why discovery skips
  `DONE-`/`completed`.
