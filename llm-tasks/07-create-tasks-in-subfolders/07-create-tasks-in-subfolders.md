# Create new LLM tasks in their own subfolder (`<id>/<id>.md`), not flat at the tasks-dir root

Visual Relay already supports folder-based tasks — an `<id>/` directory whose canonical
markdown is `<id>/<id>.md`, with any sibling files treated as attachments — but the "New task"
action still writes a **flat** `llm-tasks/<id>.md`. Make the subfolder the canonical, default
form so every new task can hold attachments alongside its markdown with no later promotion
step. Existing flat tasks keep working.

## Current state (researched)
- New tasks are written **flat** by `RelayTaskWriter.CreateAsync` → `llm-tasks/<slug>.md`
  (`src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`; its docstring even says "Creates a flat
  task file"). It is called from the New-task flow `MainWindowViewModel.CreateNewTaskAsync`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs:187-215`).
- The **nested form already exists end to end**:
  - `RelayTaskWriter.PromoteToNestedAsync` creates `llm-tasks/<id>/<id>.md`, moves the
    content, and deletes the flat file — used today only when a flat task receives its first
    attachment (`RelayTaskWriter.AddAttachmentAsync`; `Authoring.cs:99-112`).
  - Discovery treats a subfolder as one task with canonical `<folder>/<folder>.md` (fallback:
    first `.md`), all other files becoming siblings/attachments — `RelayTaskRepository.Walk`
    → `EmitSingleTaskFromFolder` (`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:165-234`);
    top-level `.md` files are the flat/legacy path (`:185-190`).
  - `RelayTaskWriter.ValidateSlug` already rejects collisions against **both** `<slug>.md` and
    `<slug>/`.
  - Archival already handles nested folders (`Directory.Move` to `completed/batch-N/<id>/`) —
    `TaskCompletionArchive.cs:65-67,125-127`; run state and metrics key off `task.Id`, which is
    identical in either layout.

## What to build

TDD — update/extend the failing tests first.

1. **Make `CreateAsync` write the nested layout** (`RelayTaskWriter.cs`): create the `<slug>/`
   directory and write `<slug>/<slug>.md` inside it (the same target shape as
   `PromoteToNestedAsync`), return that path, and update the docstring. Preserve the existing
   `ValidateSlug` checks (both-form collision, reserved prefixes, unsafe chars). Consider
   factoring the shared `<id>/<id>.md` path construction so `CreateAsync` and
   `PromoteToNestedAsync` agree.
2. **Keep the New-task flow working** (`CreateNewTaskAsync`, `Authoring.cs:187-215`): after
   `CreateAsync` returns the nested path, the list reload picks up the folder task (same `Id`)
   and selection works unchanged. New tasks are already nested, so `AddAttachmentAsync` drops
   files straight into the folder (no promotion needed); the promotion path remains for legacy
   flat tasks.
3. **Backward compatibility:** continue discovering existing flat top-level `.md` tasks
   (`RelayTaskRepository.cs:185-190`) so the many existing flat tasks (and any user's flat
   tasks) still load and run. Do **not** force-migrate archived `DONE-*.md`.
4. **(Optional, recommended) converge lazily:** when an **active** (non-archived) flat task is
   selected/edited/run, call the existing `PromoteToNestedAsync` so the convention spreads over
   time without a disruptive bulk move. Gate it to never touch archived/`DONE-` tasks.

## Done when
- Clicking "New", entering a title, and creating yields `llm-tasks/<slug>/<slug>.md` (a folder
  containing the markdown) — not a flat `llm-tasks/<slug>.md`. The new task appears in the
  queue, is selectable and runnable, and accepts attachments that land in its folder with no
  separate promotion step.
- Existing flat tasks still load and run; archived `DONE-*` tasks are untouched.
- Slug collision still blocks a name matching an existing flat file **or** folder.
- Tests first: `RelayTaskWriter` CreateAsync test(s) assert the nested `<slug>/<slug>.md` path
  (and the `<slug>/` directory is created); discovery emits the new folder task with the right
  `Id` and empty siblings; `ValidateSlug` still catches both-form collisions; (if implemented)
  an active flat task promotes on select/edit. Existing nested-discovery, attachment, and
  archival tests stay green.
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit subject (e.g.
  `feat(tasks): create new tasks in their own subfolder`).
