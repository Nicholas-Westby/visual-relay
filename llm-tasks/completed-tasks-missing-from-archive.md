# Completed tasks never appear in the Archive view

When a task finishes, the center pane shows it committed, but the **ARCHIVE**
panel (the queue panel toggled via the Archive/Queue button) stays empty — it
reads `0` even though dozens of tasks have completed. Completed work effectively
vanishes from the UI, so there is no in-app record of what has shipped.

## Current state (researched)

A finished task is marked done by `TaskCompletionArchive.MarkDone`
(`src/VisualRelay.Core/Tasks/TaskCompletionArchive.cs`), which renames
`llm-tasks/<id>.md` → `llm-tasks/DONE-<id>.md` **in place** (top level of the
tasks dir). `Archive` then *tries* to move that file into
`llm-tasks/completed/batch-<n>/`, but only when it can determine a batch number:

```
var batch = ReadBatchNumber(content) ?? HighestCompletedBatch(rootPath, tasksDir);
if (batch is null) { return null; }   // file stays at top level as DONE-<id>.md
```

The normal task has no `batch:` header and there is no `completed/` directory yet,
so `HighestCompletedBatch` returns null, `batch` is null, and the move is skipped
— the task is left as a **top-level `llm-tasks/DONE-<id>.md`**.

The archive listing in `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs` (the
method that builds the archived task list) only ever scans the `completed/`
subtree:

```
var completedRoot = Path.Combine(RootPath, config.TasksDir, "completed");
if (!Directory.Exists(completedRoot)) { return []; }
var allFiles = Directory.EnumerateFiles(completedRoot, "DONE-*.md", SearchOption.AllDirectories);
```

So top-level `DONE-*.md` files are never discovered as archived tasks. With no
`completed/` directory present, the listing returns an empty array and the
ARCHIVE panel (`ShowArchive` in `MainWindowViewModel`) shows `0`. The tasks dir
currently holds ~27 stranded top-level `DONE-*.md` files, none of which appear.
`IsSkippedName` already treats a `DONE-`/`IGNORE-` prefix as "not a pending
task", so these files are correctly excluded from the QUEUE — they are simply
missing from the ARCHIVE.

## What to build

Treat **any `DONE-<id>.md` under the tasks dir as an archived task**, whether it
sits at the top level or inside `completed/batch-<n>/`. The optional batch move
is just a grouping; archive *visibility* must not depend on it.

- Extend the archived-listing method in `RelayTaskRepository` so it discovers
  `DONE-*.md` both directly under `TasksDir` and recursively under `completed/`
  (when it exists), unioning the two. Keep the existing per-directory grouping so
  a nested archived task (`completed/batch-n/<id>/<id>.md` + sibling `DONE` files)
  still yields exactly one task with the rest as siblings, and keep skipping the
  `_ideation` directory. A top-level `DONE-<id>.md` becomes an archived
  `RelayTaskItem` with `IsArchived: true` and `Id` derived by stripping the
  `DONE-` prefix, exactly as the `completed/` path already does
  (`ArchivedTaskFromPath`).
- Do not regress the QUEUE: pending discovery (`Walk`) must still skip
  `DONE-*`/`IGNORE-*` and the `completed`/`_ideation` directories, so a task
  never shows in both QUEUE and ARCHIVE.
- Order archived tasks newest-first by last-write time, as the current listing
  does, so the just-finished task lands at the top.
- No change to the write/`Archive` path is required for visibility; leave the
  batch-move behavior as-is.

## Done when

- The ARCHIVE panel lists every completed task: top-level `llm-tasks/DONE-<id>.md`
  files AND any under `completed/batch-<n>/` both appear, newest first; the count
  chip reflects the real number instead of `0`.
- Selecting an archived task shows its markdown read-only (editing stays blocked
  for archived tasks) and its run history, as for any archived task today.
- A task that just completed appears in the ARCHIVE immediately on the next
  refresh, and never appears in the QUEUE.
- Existing canonical nested archived tasks under `completed/` still resolve to a
  single archived entry with the right siblings — no duplicates, no phantom
  entries from sibling `DONE-*.md` files.
- Tests cover: a top-level `DONE-<id>.md` is listed as archived; a task under
  `completed/batch-n/` is still listed; both are returned together when both
  exist; pending discovery still excludes `DONE-*` and `completed/`. Write the
  failing tests first.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
  Commit subjects.
