# Folder tasks whose markdown is DONE-renamed resurrect as pending queue items

Found 2026-06-10 by the first no-arg `DrainQueue` run on this repo: the drain picked up
three "pending" tasks with ids `DONE-add-app-icon`, `DONE-fix-add-attachment-button`,
`DONE-fix-new-button` and began planning them — all three are long-completed tasks
(commit d94da03) whose llm-tasks folders contain only a `DONE-*.md` (plus stray
non-markdown files like `.DS_Store` or an attachment zip).

Root cause is an asymmetry in `RelayTaskRepository`: `IsSkippedName` (DONE-/IGNORE-
prefixes) is applied to top-level entries and to folder names, but
`EmitSingleTaskFromFolder`'s canonical-markdown fallback ("first .md in the folder")
considers DONE-/IGNORE- prefixed files. A folder named `add-app-icon/` holding only
`DONE-add-app-icon.md` therefore emits a pending task whose id is the DONE filename
stem. Every consumer of `ListPendingAsync` (GUI queue, DrainQueue) sees completed work
as pending; only explicit-task-id drives were immune, which is why this stayed latent.

## Goal

Task discovery treats a retired folder task exactly like a retired top-level task:

- A folder whose canonical markdown resolution finds only DONE-/IGNORE- prefixed .md
  files is never emitted as pending. A DONE-canonical folder surfaces through
  `ListCompletedAsync` (like stranded top-level `DONE-*.md` files already do).
- The fallback "first .md in folder" never selects a DONE-/IGNORE- prefixed file while
  a non-prefixed .md exists; if a non-prefixed .md exists alongside DONE residue, the
  folder is still one pending task built from the non-prefixed file.
- Invariant worth a test of its own: no item returned by `ListPendingAsync` ever has
  an id starting with `DONE-` or `IGNORE-` (this is the property the drain relied on).

## Approach (suggested)

- Route the folder fallback through the same `IsSkippedName` rule used elsewhere;
  classify all-DONE folders as archived in `EmitSingleTaskFromFolder` (or skip there
  and pick them up in `ListCompletedAsync`'s walk).
- Tests in the existing repository test suite: folder with only `DONE-x.md` →
  absent from pending, present in completed; folder with `DONE-x.md` + `y.md` →
  pending task from `y.md`; folder named `DONE-x/` stays skipped (existing rule);
  the ListPendingAsync id-prefix invariant.
