# Fix "Add Attachment…" silently doing nothing (the file is saved, but the wrong task ends up selected)

Adding an attachment to a task appears to do nothing: you click **Add Attachment…**, pick a
screenshot, the picker closes — and the Attachments tab shows no new file. The file is **not**
lost; it is copied to disk correctly. The bug is in what happens *after* the copy: the refresh
resets the queue selection to the first task, so you end up looking at a *different* task's
(usually empty) attachments and conclude nothing happened. This reproduces whenever the task you
attached to is not the alphabetically-first task in the queue.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; if a snippet has drifted, re-read the file and adapt.

**The add path copies the file, then refreshes without preserving selection.**
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs`, method `AddAttachmentsAsync`:

- Picks files via `var files = await _filePicker.PickFilesAsync();` and returns early on
  `if (files.Count == 0)`.
- Copies each file with `await RelayTaskWriter.AddAttachmentAsync(currentTask, file);` (the file
  genuinely lands in the task folder — confirmed correct).
- Ends with `await RefreshAsync();` **and nothing re-selects the task that was just edited.**

`RemoveAttachmentAsync` in the same file has the identical bug: it ends with `RelayTaskWriter.RemoveAttachment(filePath);` then `await RefreshAsync();` with no selection preservation.

**Why the selection jumps.** `src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs`,
`RefreshAsync` calls `await ReloadTaskListAsync();` (no argument). In
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs`, `ReloadTaskListAsync` ends with:

```csharp
SelectedTask = preferredTaskId is null
    ? Tasks.FirstOrDefault()
    : Tasks.FirstOrDefault(task => task.Id == preferredTaskId) ?? Tasks.FirstOrDefault();
```

With `preferredTaskId == null`, selection snaps to `Tasks.FirstOrDefault()`. Tasks are ordered
alphanumerically by id (`RelayTaskRepository.ListAsync` ends `.OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase)`), so unless you were on the first task you get moved away.

**Why the moved-away view looks empty.** Changing `SelectedTask` fires
`OnSelectedTaskChanged` (Commands.cs), which calls `RebuildAttachments(value)`
(Helpers.cs) — it clears `Attachments` and repopulates from the *newly selected* task's
`SiblingPaths`. So you now see the first task's attachments, not yours.

**The fix pattern already exists in this file.** `CreateNewTaskAsync` (Authoring.cs) does
`await RefreshAsync();` and then re-selects the just-created task:

```csharp
SelectedTask = Tasks.FirstOrDefault(t =>
    string.Equals(t.Id, slug, StringComparison.OrdinalIgnoreCase));
```

Attachment add/remove should preserve selection the same way. Note the task **id is stable across
the flat→nested promotion** that `AddAttachmentsAsync` performs (the id is the folder name, which
is unchanged), so re-selecting by `currentTask.Id` always finds it.

**Secondary, separate failure mode — a silent drop with no feedback.**
`src/VisualRelay.App/Services/AvaloniaFilePicker.cs` returns
`files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray()`. `TryGetLocalPath()` can
return `null` for non-local backings (some macOS picker sources), in which case a *picked* file is
dropped and `AddAttachmentsAsync` hits `files.Count == 0` and returns — indistinguishable from the
user cancelling. Either way the user gets no explanation. This is a real "nothing happened"
path even after the selection bug is fixed, so harden it too.

**Test seam.** `tests/VisualRelay.Tests/AddAttachmentsTests.cs` already constructs the VM with a
fake `IFilePicker` and exercises `CanAddAttachments` / a headless no-op
(`AddAttachmentsAsync_Headless_NoOps_WithoutCrash`). Reuse that construction; what's missing is an
end-to-end test that a *successfully* added file becomes visible on the task you added it to.

## What to build

TDD — write the failing test first.

### 1. Preserve the edited task's selection across the refresh (primary fix)
In `AddAttachmentsAsync` and `RemoveAttachmentAsync`, keep the user on the task they edited so the
new/removed attachment is reflected immediately. Prefer reloading directly to the known id rather
than refresh-then-reselect (no selection flicker, no needless backend re-probe):

- Capture the stable id before refreshing (in add: `currentTask.Id`; in remove: the current
  `SelectedTask?.Id`).
- Replace the bare `await RefreshAsync();` with a refresh that targets that id — e.g. call
  `await ReloadTaskListAsync(<id>);`, or, if you keep `RefreshAsync()` for its backend re-probe,
  re-select afterward exactly as `CreateNewTaskAsync` does. Either is acceptable; the test below is
  the arbiter.

### 2. No silent no-op when a pick yields nothing usable (secondary hardening)
Make "I picked a file but nothing attached" impossible to hit silently. Distinguish *cancelled*
(0 chosen — stay silent) from *chosen-but-unusable* (chosen > 0, but all `TryGetLocalPath()`
returned null). Surface the latter through the VM's existing operation-message channel
(`StatusText`), e.g. `"Couldn't attach: the selected item has no local file path."` This likely
needs the picker to report how many entries were chosen vs. how many resolved to a path (small
shape change to `IFilePicker`/its result, or a second return value), so the VM can tell the cases
apart. Keep the change minimal and covered by a test.

## Tests / verification
- **Red→green (primary):** in `AddAttachmentsTests.cs`, with a multi-task queue whose target task
  is **not** first alphabetically, inject a fake picker returning a real temp file path, run
  `AddAttachmentsAsync`, then assert **(a)** `SelectedTask?.Id` still equals the target task and
  **(b)** `Attachments` contains a row whose path ends with the picked file name. This fails today
  (selection jumps to the first task; `Attachments` shows the wrong task).
- **Remove keeps selection:** analogous test for `RemoveAttachmentAsync` (use the test confirm-hook
  so it doesn't prompt) asserting selection is unchanged afterward.
- **Secondary:** a fake picker that simulates "one entry chosen, zero local paths resolved" sets a
  clear `StatusText`, while a "zero chosen" (cancel) leaves `StatusText` untouched.
- Existing `AddAttachmentsTests` (CanExecute, headless no-op) stay green.

## Done when
- Adding an attachment to **any** task leaves that task selected and the file visible in the
  Attachments tab immediately — no manual Refresh or re-click needed.
- Removing an attachment likewise keeps you on the same task.
- A file that was chosen but couldn't be attached produces a visible reason, never a silent no-op.
- Tests first (the primary visibility test fails before the fix, passes after).
- `./visual-relay check` green; changed files < 300 lines; compiled bindings clean; Conventional
  Commit subject (e.g. `fix(attachments): keep the edited task selected so added files appear immediately`).

## Notes
- Do **not** delete or relocate any attachment files already on disk from previous failed-looking
  adds — they are real and should simply become visible once selection is preserved.
- The flat→nested promotion inside `AddAttachmentsAsync` is correct; leave it. Just make sure the
  id you re-select by is the promoted task's id (unchanged by promotion).
- Out of scope: redesigning the Attachments tab UI or the picker beyond the small reporting change
  in item 2.
