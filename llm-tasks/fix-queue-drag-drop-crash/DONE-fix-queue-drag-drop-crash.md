# Drag-to-reorder crashes the whole app on macOS (empty pasteboard)

Starting a drag on a queue task card terminates the entire application with an uncaught native
exception. This makes the recently-added drag-to-reorder gesture unusable on macOS — the platform
Visual Relay actually ships on — so treat it as **high severity**. Reproduce by pressing and
dragging any task card in the queue list.

The crash (see attached `Screenshot 2026-06-16 at 10.09.11 PM.png`):

```
*** Terminating app due to uncaught exception 'NSGenericException', reason:
'There are 0 items on the pasteboard, but 1 drag images. There must be 1 draggingItem per pasteboardItem.'
…
 3  AppKit                   -[NSDraggingSession(NSInternal) _initWithPasteboard:draggingItems:clippingRect:source:]
 4  AppKit                   -[NSCoreDragManager beginDraggingSessionWithItems:fromWindow:withClipRect:event:source:]
 5  AppKit                   -[NSView(NSDrag) beginDraggingSessionWithItems:event:source:]
 6  libAvaloniaNative.dylib  TopLevelImpl::BeginDragAndDropOperation(AvnDragDropEffects, AvnPoint, IAvnClipboardDataSource*, …)
…
23  libAvaloniaNative.dylib  -[AvnView mouseEvent:withType:]
24  libAvaloniaNative.dylib  -[AvnView mouseDown:]
libc++abi: terminating due to uncaught exception of type NSException
```

## Current state (researched)

> **Freshness contract.** Verify references by searching for the quoted strings, not line numbers.

**Root cause: the drag payload is in-process only, so the macOS pasteboard is empty.**
`src/VisualRelay.App/Views/Controls/QueuePanel.axaml.cs`:

```csharp
// In-process format: the dragged row reference never leaves the app, so it is
// never serialized to the platform clipboard / OS drag buffer.
private static readonly DataFormat<TaskRowViewModel> TaskRowFormat =
    DataFormat.CreateInProcessFormat<TaskRowViewModel>("visual-relay/task-row");
…
var data = new DataTransfer();
data.Add(DataTransferItem.Create(TaskRowFormat, row));
await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
```

That comment is exactly the problem: an in-process format puts **nothing** on the OS pasteboard.
Avalonia's macOS backend (`libAvaloniaNative.dylib` `TopLevelImpl::BeginDragAndDropOperation`)
then calls AppKit `-[NSView beginDraggingSessionWithItems:event:source:]` with a drag image but
**zero** pasteboard items, and AppKit asserts: "There are 0 items on the pasteboard, but 1 drag
images." → `NSGenericException`.

**The existing `try/catch` does NOT (and cannot) save this.** `BeginDragAsync` wraps
`DoDragDropAsync` in `catch (Exception ex)`, but the exception is an Objective-C/AppKit exception
raised on the native side; it unwinds through `libc++abi` and **terminates the process**
(`libc++abi: terminating due to uncaught exception of type NSException`) before any managed catch
can run. A defensive catch is therefore *not* a fix — the fix must prevent AppKit from asserting
in the first place by giving the drag session at least one pasteboard item.

**The app is Avalonia 12.0.4** (`src/VisualRelay.App/VisualRelay.App.csproj`), whose new clipboard
API exposes a well-known serializable format **`DataFormat.Text`** (confirmed present in
`Avalonia.Base` 12.0.4). Adding a real item in that format makes the macOS backend write a
pasteboard item, satisfying AppKit's "1 draggingItem per pasteboardItem" contract.

**Drop logic keys on the in-process format and won't be affected by an extra item.**
`OnDragOver` sets `DragDropEffects.None` unless `e.DataTransfer.Contains(TaskRowFormat)`; `OnDrop`
reads `e.DataTransfer.TryGetValue(TaskRowFormat)`. So an additional text item is inert for our
logic — external (non-task) drags are still rejected, and dragging a card onto another app would
at worst drop the task id as text (harmless).

## What to build

1. **Add a pasteboard-backed item to the drag `DataTransfer`** so macOS gets ≥1 pasteboard item.
   In `BeginDragAsync` (or the extracted seam below):

   ```csharp
   var data = new DataTransfer();
   data.Add(DataTransferItem.Create(TaskRowFormat, row));          // in-process: drives the reorder
   data.Add(DataTransferItem.Create(DataFormat.Text, row.Id));     // pasteboard-backed: satisfies AppKit
   ```

   Verify the exact `DataFormat.Text` member/overload against Avalonia 12.0.4 and adapt the value
   if needed (any non-empty string payload works; `row.Id` is the natural choice). The goal is
   simply that the `DataTransfer` contains at least one **non-in-process** (serializable) format.

2. **Extract a testable seam** for building the drag data, e.g.
   `internal static DataTransfer BuildDragData(TaskRowViewModel row)`, and call it from
   `BeginDragAsync`. This lets a headless test assert the payload composition without driving real
   pointer input (the AppKit crash itself isn't reproducible headless).

3. **Keep** the `try/catch` around `DoDragDropAsync` (cheap insurance for *managed* faults) but do
   not treat it as the fix — note in a comment that native AppKit exceptions can't be caught here.

Leave `MoveTask`, `OnDragOver`, `OnDrop`, drop-target highlighting, and the `ReorderEnabled` gate
unchanged.

## Tests / verification (TDD — failing test first)

- **Regression guard:** assert `BuildDragData(row)` contains BOTH `TaskRowFormat` **and** a
  serializable format (`DataFormat.Text`). Before the fix the payload has only the in-process
  format → test red; after → green. (A headless test can't reproduce the AppKit termination, so
  this composition assertion is the regression anchor.)
- **Manual smoke on macOS (note in PR):** drag a queue card across several positions → no crash,
  the order updates, and **Run All** honors the new order; a plain click still just selects the
  task.
- `./visual-relay check` green.

## Decisions (settled)

1. **Fix by populating the pasteboard (`DataFormat.Text`), not by catching the exception.** *Why:*
   it is a native AppKit `NSException` that terminates the process before managed `catch` runs; the
   only reliable fix is to satisfy the drag-session contract with ≥1 pasteboard item.
2. **Keep the in-process format as the reorder source of truth; the text item exists only to
   populate the pasteboard.** *Why:* avoids serializing the view model, preserves the existing drop
   logic, and the extra text payload is harmless.
3. **Scope = stop the crash.** Do not redesign the gesture or revert to the old Up/Down buttons.

## Notes

- Direct follow-up to `queue-drag-drop-reorder` (DONE). All edits live in
  `src/VisualRelay.App/Views/Controls/QueuePanel.axaml.cs`.
- macOS is the shipping platform and the app dies on the *first* drag, so prioritize accordingly.
