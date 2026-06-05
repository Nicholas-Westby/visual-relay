# Author, edit, and manage attachments for LLM tasks from the UI

Today Visual Relay is read-only over the task backlog: you can browse, reorder,
and run tasks, but you cannot **create** a new task, **edit** an existing one, or
**manage the attachments** (the other files that travel with a task). All of that
has to happen in an editor/Finder outside the app. This task adds an in-app
authoring surface for all three, and fixes discovery so that a task folder can
hold extra markdown files as attachments instead of mistaking them for separate
tasks.

The data model already distinguishes two task shapes
(`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:83` `Walk`):

- **Flat task** — a single markdown file `llm-tasks/<slug>.md`.
- **Nested task** — a folder `llm-tasks/<slug>/<slug>.md` plus sibling files. The
  small text siblings are inlined into the task's Context (`ReadTaskInputAsync` →
  `BuildContext`, `RelayTaskRepository.cs:76` and `:194`, bounded by
  `TextExtensions`/`PerFileContextLimit` 8 000 /`TotalContextLimit` 24 000 at
  `RelayTaskRepository.cs:9-11`). A `RelayTaskItem` already carries
  `MarkdownPath`, `TaskDirectory`, `IsNested`, and `SiblingPaths`
  (`src/VisualRelay.Domain/RelayTaskItem.cs`).

The read side of attachments exists; what is missing is the write side, an edit
surface, and a discovery fix for extra markdown.

## Current state (researched)

- **No authoring.** Nothing in the app writes a task file; the only writer of the
  nested layout is a test double (`tests/VisualRelay.Tests/TestDoubles.cs`
  `WriteNestedTask`). The production precedent to mirror is `RelayConfigWriter`
  (`src/VisualRelay.Core/Init/RelayConfigWriter.cs`) — a small pure writer the
  view model calls before refreshing.
- **No editing.** The selected task's markdown is shown **read-only**: a
  `TextBlock Text="{Binding SelectedTaskMarkdown}"` in the "Markdown" tab
  (`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:98-122`), populated
  by `LoadSelectedTaskAsync` (`MainWindowViewModel.Commands.cs:147-163`). There is
  no editable field and no save path back to `task.MarkdownPath`.
- **No attachment management.** The "Context" tab shows the inlined siblings, but
  nothing lets you add, remove, or reveal one. A reveal-in-Finder service already
  exists to reuse: `FileReveal` / `RevealStageArtifactsCommand`
  (`MainWindowViewModel.Commands.cs:176-201`, shipped by
  `DONE-reveal-stage-artifacts-in-finder.md`).
- **Extra markdown in a task folder is mis-discovered as a separate task.** `Walk`
  (`RelayTaskRepository.cs:83`) emits *every* `.md` in a folder as its own task,
  and builds `SiblingPaths` from non-`.md` files only
  (`RelayTaskRepository.cs:98-104`). So `llm-tasks/<slug>/notes.md` next to
  `llm-tasks/<slug>/<slug>.md` shows up as a phantom second queue entry instead of
  an attachment of `<slug>`.
- Tasks live under `<root>/llm-tasks` (`RelayConfigLoader.Defaults` TasksDir);
  discovery skips `DONE-*`/`IGNORE-*` names and `completed`/`_ideation` dirs
  (`RelayTaskRepository.cs:8`, `IsSkippedName`). Every write must be followed by
  `ReloadTaskListAsync`/`RefreshAsync`.

## What to build

All filesystem work goes in one pure, unit-tested Core unit: `RelayTaskWriter`
(`src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`), called from new view-model
commands and a modest detail-pane UI.

### Fix discovery first: one task per folder, the rest are attachments

Adopt and enforce a single rule: **within a task subfolder, the task markdown is
the file named after the folder (`<folder>/<folder>.md`); every other entry in
that folder — including other `.md` files — is an attachment.** Top-level `.md`
files directly in `llm-tasks/` stay flat tasks (unchanged).

- Change `Walk` so that when it descends into a task subfolder it emits exactly
  one `RelayTaskItem` (the folder-named markdown) and collects *all* other entries
  as `SiblingPaths` — drop the `.md` exclusion in the sibling filter
  (`RelayTaskRepository.cs:98-104`). Apply the same "other markdown = attachment"
  rule to `ArchivedTaskFromPath` (`RelayTaskRepository.cs:131-150`) for completed
  tasks. If a legacy folder has no file matching its name, the single markdown it
  contains is the task; never surface a non-task markdown as its own queue entry.
- `BuildContext` already lists `"md"` in `TextExtensions`, so once other `.md`
  siblings are in `SiblingPaths` they inline into Context automatically (subject
  to the existing 8 KB/24 KB caps). A task's own markdown is never an attachment
  of itself.

### Author a new task

- Add a **New task** button to the queue panel header
  (`src/VisualRelay.App/Views/Controls/QueuePanel.axaml`), next to the Archive
  toggle. It collects a title and an initial markdown body.
- `RelayTaskWriter.CreateAsync(root, slug, markdown)` writes
  `llm-tasks/<slug>.md` (flat). Derive `<slug>` from the title as filesystem-safe
  kebab-case. Reject and surface an inline error (writing nothing) when the slug
  is empty, unsafe, carries a reserved `DONE-`/`IGNORE-` prefix, or collides with
  an existing flat file or task folder.
- After the write, `RefreshAsync` and select the new task so it is immediately
  runnable.

### Edit an existing task

- Give the "Markdown" tab an explicit **Edit → Save** model: an Edit button swaps
  the read-only `TextBlock` for a multiline `TextBox` bound to an edit buffer;
  Save writes the buffer to `task.MarkdownPath` via
  `RelayTaskWriter.SaveAsync(task, markdown)`, then reloads so Context
  re-derives. Cancel discards the buffer.
- Block editing of the **currently running** task (`_runningTaskId == task.Id`)
  and of **archived/DONE** tasks, with a visible reason; the field stays
  read-only in those states.

### Manage attachments

- Add an **Attachments** tab to the detail pane that lists the task's
  `SiblingPaths` (filename + size), each row with **Reveal in Finder** (reuse
  `FileReveal`) and **Remove** (delete the file after a confirmation).
- **Add attachment**: a native file picker copies the chosen file(s) into the
  task's folder. When the task is currently **flat**, adding the first attachment
  promotes it to nested — create `llm-tasks/<slug>/`, move `<slug>.md` →
  `<slug>/<slug>.md`, then place the attachment beside it — so discovery keeps it
  as one task and Context picks the new file up.
- Refresh after every add/remove so the queue and Context reflect the new set.

## Done when

- **Discovery:** a markdown file inside a task folder that is not the
  folder-named task (e.g. `llm-tasks/<slug>/notes.md`) is treated as an attachment
  of `<slug>` and inlined into its Context — never listed as a separate task; the
  folder-named markdown is the only queue entry for that folder. Existing
  canonical nested tasks (`<slug>/<slug>.md` + non-markdown siblings) are
  unchanged.
- **Author:** the New task flow creates `llm-tasks/<slug>.md`, it appears in the
  queue immediately and is runnable; empty/unsafe/reserved-prefix/colliding slugs
  are rejected with a clear message and write nothing.
- **Edit:** the selected task's markdown can be edited and saved back to its
  `MarkdownPath`; the change persists and Context re-derives on reload; editing is
  blocked (with a visible reason) for the running task and for archived/DONE
  tasks.
- **Attachments:** the Attachments tab lists a task's files with Reveal and
  Remove; adding an attachment copies it into the task folder, promoting a flat
  task to the nested `llm-tasks/<slug>/<slug>.md` layout on first add; removes
  delete the file after confirmation; Context reflects adds/removes within the
  existing inlining limits.
- **No regressions:** discovery still skips `DONE-*`/`IGNORE-*`/`completed`/
  `_ideation`; running, reordering, archiving, and run-history behave as before;
  a flat task that never gains an attachment stays a single file.
- `RelayTaskWriter` is a pure, unit-tested Core unit (create, save,
  promote-to-nested, add/remove attachment, slug validation, collision handling),
  and the discovery change has tests proving an extra `.md` in a task folder is an
  attachment, not a task. Write the failing tests first; cover the view-model
  commands and edit guards too.
- Verify with `./visual-relay screenshot` (and eyeball `./visual-relay launch`
  for the editable field and the attachment list if possible).
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
  Commit subjects.
