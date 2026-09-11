# create-task must leave the new task listed, whatever view is showing

`POST /command/create-task` answers `{"ok":true}` only once `/state.tasks[]`
lists the new task, and the GUI's Create button makes the same promise by
reloading and selecting the task it wrote. Both break while the archive view is
showing: the reload lists the `DONE-*` archives, the fresh task is nowhere, the
API answers with a path it constructed instead of one it read, and the selection
silently lands on an unrelated archived task. Creating a task is a queue action.
It switches to the queue view and then keeps its promise.

## Evidence

- Review of 2026-09-11 (item 10): with the archive showing, `create-task`
  returns 200 and the task is absent from `/state.tasks[]`; `ResolveTaskPath`
  falls back to the canonical layout.
- `ControlApi.Tasks.cs:72-76` names the case in its own comment ("as when the
  archive view is showing"); `ControlApiCreateTaskTests.cs` has no test with
  `ShowArchive` set (`grep -n "ShowArchive\|archive"` is empty).

## Current state (researched at 5b85640b)

- `src/VisualRelay.App/Services/ControlApi.Tasks.cs:24-70`
  `InvokeCreateTaskAsync`: validates the title (`:26-30`), refuses while busy
  (`:36-39`), sets `NewTaskTitle`/`NewTaskBody` (`:46-48`), awaits
  `CreateNewTaskCommand` (`:59`), then answers with `ResolveTaskPath(slug)`
  (`:68-69`). `ResolveTaskPath` (`:77-79`) reads the row's `MarkdownPath` or
  falls back to `Path.Combine(RootPath, "llm-tasks", slug, slug + ".md")`. The
  doc at `:14-15` promises the task is "already present in /state.tasks[] when
  the response is written".
- `ViewModels/MainWindowViewModel.Authoring.cs:253-284` `CreateNewTaskAsync`
  writes through `RelayTaskWriter.CreateAsync` (`:273`) and calls
  `ReloadTaskListAsync(slug)` (`:283`). `CanCreateNewTask` (`:289-290`) checks
  the title and the root only.
- `ViewModels/MainWindowViewModel.Helpers.cs:141` `ReloadTaskListAsync`:
  `:169-171` lists `repository.ListCompletedAsync()` when `ShowArchive`, else
  the ordered queue; `:182-184` selects the preferred id or falls back to
  `Tasks.FirstOrDefault()`.
- `Core/Tasks/RelayTaskRepository.cs:45` `ListCompletedAsync` enumerates
  `DONE-*.md` only. `ShowArchive` is `MainWindowViewModel.cs:144`, toggled by
  `ToggleArchiveAsync` (`MainWindowViewModel.Commands.cs:91`, API
  `archive-toggle`); its change hook lives in `MainWindowViewModel.MarkDone.cs:42`.
- Tests: `tests/VisualRelay.Tests/ControlApiCreateTaskTests.cs` (ten facts;
  `:92` asserts the listing from the queue view only).

## Prescribed approach

1. `CreateNewTaskAsync`: after the write and before the reload, when
   `ShowArchive` is true set it false. Check the `ShowArchive` change hook first
   (`MarkDone.cs:42`): if it reloads the list itself, let that reload carry the
   preferred id and do not reload twice. The rule lives in the view model so the
   dialog's Create button and the API get it together.
2. `ResolveTaskPath`: delete the fallback. After the reload the row must exist;
   when it does not, answer `500 {"ok":false,"command":"create-task","error":
   "task written at <path> but not listed"}`. A failure the caller can see, not
   a constructed path.
3. The status text after a creation that switched views reads
   `Created <slug>; switched to the queue`.
4. AGENTS.md, the `create-task` entry: one clause saying the app switches to the
   queue view when the archive is showing, so the answer always comes from the
   listed row.

## Tests

- `ControlApiCreateTaskTests.CreateTask_WhileTheArchiveIsShowing_SwitchesToTheQueueAndListsIt`:
  toggle the archive, create, then assert `ShowArchive` is false,
  `/state.tasks[]` contains the slug, the answer's `path` equals the row's
  `MarkdownPath`, and `SelectedTask.Id` is the slug.
- `MainWindowViewModel` authoring test
  `CreateNewTask_FromTheArchiveView_SwitchesToTheQueueAndSelectsTheNewTask`
  (the GUI path, no control server).
- The existing `CreateTask_LeavesTheTaskInStateWhenTheCallReturns` keeps
  passing from the queue view.

## Verification (through the control API)

    curl -s -X POST -d '{"path":"<project>"}' http://127.0.0.1:8765/command/open-folder
    curl -s -X POST http://127.0.0.1:8765/command/archive-toggle
    curl -s -X POST -d '{"title":"Probe task","body":"probe\n"}' http://127.0.0.1:8765/command/create-task
    curl -s http://127.0.0.1:8765/state | jq '{sel: .selectedTask.id, ids: [.tasks[].id]}'
    curl -s http://127.0.0.1:8765/screenshot -o /tmp/vr-create.png

Expected: the answer's `path` is the nested markdown path, `ids` contains
`probe-task`, `sel` is `probe-task`, and the screenshot shows the queue, not the
archive. Delete the probe task afterwards.

## Out of scope

A `tasksDir` other than `llm-tasks` (the writer at `RelayTaskWriter.cs:103`
hardcodes it too; a separate inconsistency); the archive view's own behavior.

## Rejected alternatives

- Answer 409 while the archive is showing: the caller cannot see the view and
  its request is legitimate.
- Keep the fallback and add a `listed:false` field: a constructed path is a
  claim the API cannot back, and every caller would have to handle a second
  shape.
