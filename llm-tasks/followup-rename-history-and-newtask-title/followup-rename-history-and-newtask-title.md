# Task rename orphans run history; new-task-with-body shows slug instead of title

Two adjacent correctness gaps surfaced by review of the `show-full-task-name` feature
(commit `42a45f8`, which added a detail-panel task name that is editable, with save
renaming the task's markdown file and subfolder). The feature itself works; these are
follow-ups.

## Problem 1 — renaming a task severs its run history

`RelayTaskWriter.RenameAsync` renames the task's `llm-tasks/<slug>/` subfolder and its
`<slug>.md`, but does **not** move the task's run-history directory `.relay/<slug>/`.
Run artifacts/metrics/status/seals/ledger are keyed by task id under
`.relay/<taskId>/` (see `RelayRunHistory`, which builds paths like
`Path.Combine(rootPath, ".relay", taskId)`). So renaming a task that has already run
severs its history — the detail pane then shows "No run history", and a stale
`.relay/<old-slug>/` directory is left behind.

**Fix:** when renaming, if `.relay/<old-slug>/` exists, move it to `.relay/<new-slug>/`
(in the same operation that renames the `llm-tasks` subfolder, with the same safety
checks — no overwrite of an existing destination). Also migrate the in-memory id-keyed
tracking state in `MainWindowViewModel` so a rename during the session doesn't orphan
live state: `SaveEditAsync` currently migrates only `_boostedTaskIds`. Factor a single
"re-key task id across all id-keyed maps" helper and call it for the maps that can hold
entries for a renamable (non-running) task — at minimum `_liveEventsByTask` and
`_liveTraceEntriesByTask` (others like `_runStartedAt` are for running tasks, which
`CanEditSelectedTask` already blocks from editing; include them for completeness).

## Problem 2 — a new task created *with a body* shows the slug, not the entered title

In `CreateNewTaskAsync` (`MainWindowViewModel.Authoring.cs`), the with-body branch writes
`markdown = NewTaskBody` verbatim — the entered `NewTaskTitle` is used only to derive the
slug and is then discarded. With no `# ` heading in the file, the new detail-panel name
(`ExtractTitleFromMarkdown`) falls back to the slug, so the panel shows e.g.
`my-new-task` instead of "My New Task". The empty-body branch already writes `# {title}\n`
correctly.

**Fix:** prepend the heading to the body branch too, matching `SaveEditAsync`'s format
(`# {title}\n\n{body}`): write `# {NewTaskTitle.Trim()}\n\n{NewTaskBody}`.

## Problem 3 (low, optional) — title-extraction asymmetry

`ExtractTitleFromMarkdown` scans all lines for the first `# ` heading, but
`SplitMarkdownTitle` inspects only the first line. For markdown whose heading isn't on
line 1 (e.g. a leading blank line), select shows the real title while Edit puts the slug
in the title buffer, so saving unchanged triggers a spurious rename and a duplicated
heading. Make both helpers use the same "first `# ` line" strategy.

## How to verify
- Rename a task **that has already run**: its run history (metrics/status/artifacts)
  still resolves after the rename, `.relay/<new-slug>/` exists, and no stale
  `.relay/<old-slug>/` remains. Add a test around `RenameAsync` asserting the `.relay`
  dir moves when present (and is a no-op when absent).
- Create a **new task with a body**: the detail panel "Markdown" tab shows the entered
  title (not the slug). Add/extend a test in the new-task authoring tests.
- Existing rename/title tests stay green.

## Constraints
- VR is general-purpose — no project-specific assumptions; derive everything generically.
- Keep C#/XAML files ≤300 lines (some touched files are already at the limit — relocate a
  helper if needed rather than exceeding it). `./visual-relay check` green; Conventional
  Commit subjects.
